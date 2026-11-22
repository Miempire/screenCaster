namespace ScreenCast.Core.Transport;

/// <summary>
/// Reliable ordered control delivery: sequence numbers with cumulative plus selective ACK,
/// duplicate suppression, retransmission and bounded expiry. Video never uses this channel.
/// </summary>
/// <remarks>
/// <para>
/// A retransmission reuses the same control message number but travels in a freshly protected
/// datagram, so the AEAD packet sequence and nonce never repeat. The receiver deduplicates on
/// the control message number only.
/// </para>
/// <para>
/// A duplicate Data frame is acknowledged again. Its side effect executes exactly once, but the
/// ACK is always repeated: otherwise one lost ACK would make the sender declare failure even
/// though the event did arrive.
/// </para>
/// <para>
/// One instance is one direction of one component. Keep each direction single-owner; the
/// caller supplies the already-encrypted outbound path and does the decryption before calling
/// <see cref="OnFrame"/>.
/// </para>
/// </remarks>
public sealed class ReliableControlChannel
{
    public const int WindowSize = 64;
    private const int MaxAttempts = 8;
    private const long MessageLifetimeMicroseconds = 2_000_000;
    private const long InitialRtoMicroseconds = 200_000;
    private const long MinRtoMicroseconds = 30_000;
    private const long MaxRtoMicroseconds = 1_000_000;

    private readonly Action<ReadOnlyMemory<byte>> _sendFrame;
    private readonly Action<ControlEventType, ReadOnlyMemory<byte>> _onDelivered;
    private readonly Action<ControlEventType>? _onExpired;
    private readonly Func<long> _nowMicroseconds;

    // Send side.
    private readonly Dictionary<ulong, PendingSend> _pending = [];
    private ulong _nextSequence;
    private bool _sequenceExhausted;

    // Receive side. ackBase == ulong.MaxValue means "no contiguous message yet", so
    // ackBase + 1 reads as message 0 and ackMask bit 0 means message 0.
    private ulong _ackBase = ulong.MaxValue;
    private ulong _ackMask;
    private readonly Dictionary<ulong, (ControlEventType Type, byte[] Body)> _outOfOrder = [];

    // Smoothed RTT in microseconds. -1 means no valid sample yet.
    private double _srtt = -1;
    private double _rttVar;
    private long _rto = InitialRtoMicroseconds;

    public ReliableControlChannel(
        Action<ReadOnlyMemory<byte>> sendFrame,
        Action<ControlEventType, ReadOnlyMemory<byte>> onDelivered,
        Func<long>? nowMicroseconds = null,
        Action<ControlEventType>? onExpired = null)
    {
        ArgumentNullException.ThrowIfNull(sendFrame);
        ArgumentNullException.ThrowIfNull(onDelivered);
        _sendFrame = sendFrame;
        _onDelivered = onDelivered;
        _onExpired = onExpired;
        _nowMicroseconds = nowMicroseconds ?? DefaultClock;
    }

    public int PendingCount => _pending.Count;

    public long CurrentRtoMicroseconds => _rto;

    public ulong AckBase => _ackBase;

    public ulong AckMask => _ackMask;

    private static long DefaultClock() =>
        System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Queue one event. Returns false when the send window is full or message numbers are
    /// exhausted, so the caller can drop an observational event such as mouse movement while
    /// keeping the ordered input stream intact.
    /// </summary>
    public bool TryEnqueue(ControlEventType eventType, ReadOnlySpan<byte> body)
    {
        if (_sequenceExhausted || _pending.Count >= WindowSize)
        {
            return false;
        }

        ulong sequence = _nextSequence;
        if (sequence == ulong.MaxValue)
        {
            // Message numbers must never wrap: wrapping would alias an old event.
            _sequenceExhausted = true;
            return false;
        }

        _nextSequence++;
        long now = _nowMicroseconds();
        byte[] payload = body.ToArray();
        _pending[sequence] = new PendingSend(sequence, eventType, payload, now);
        _sendFrame(ControlCodec.EncodeData(sequence, _ackBase, _ackMask, eventType, payload));
        return true;
    }

    /// <summary>Handle one already-authenticated, already-decrypted control frame.</summary>
    public void OnFrame(ReadOnlySpan<byte> frame)
    {
        if (!ControlCodec.TryDecode(frame, out var message))
        {
            return;
        }

        if (message.IsAck)
        {
            OnAck(message.AckBase, message.AckMask);
            return;
        }

        OnData(message);
    }

    /// <summary>Drive retransmission and expiry. Call from a 5-10 ms timer.</summary>
    public void Tick()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        long now = _nowMicroseconds();
        List<ulong>? expired = null;
        List<ulong>? retransmit = null;

        foreach (var item in _pending.Values)
        {
            if (now - item.FirstSentMicroseconds >= MessageLifetimeMicroseconds || item.Attempts >= MaxAttempts)
            {
                (expired ??= []).Add(item.Sequence);
                continue;
            }

            if (now - item.LastSentMicroseconds >= BackoffFor(item))
            {
                (retransmit ??= []).Add(item.Sequence);
            }
        }

        if (retransmit is not null)
        {
            foreach (var sequence in retransmit)
            {
                var item = _pending[sequence];
                item.Attempts++;
                item.LastSentMicroseconds = now;
                item.Retransmitted = true;
                _sendFrame(ControlCodec.EncodeData(sequence, _ackBase, _ackMask, item.EventType, item.Body));
            }
        }

        if (expired is not null)
        {
            foreach (var sequence in expired)
            {
                if (!_pending.TryGetValue(sequence, out var failed))
                {
                    continue;
                }

                _pending.Remove(sequence);
                // The caller must release held keys: an input event that never arrived cannot
                // be assumed inert.
                _onExpired?.Invoke(failed.EventType);
            }
        }
    }

    /// <summary>
    /// Per-message exponential backoff capped at four doublings, so one stuck message does not
    /// flood the link while later messages keep flowing.
    /// </summary>
    private long BackoffFor(PendingSend item)
    {
        int shifts = Math.Min(item.Attempts, 4);
        long value = _rto << shifts;
        return Math.Min(value, MaxRtoMicroseconds);
    }

    private void OnAck(ulong ackBase, ulong ackMask)
    {
        ApplyAcknowledgement(ackBase, ackMask);
        FastRetransmit(ackBase, ackMask);
    }

    private void ApplyAcknowledgement(ulong ackBase, ulong ackMask)
    {
        // ackBase == ulong.MaxValue means the peer has no contiguous message yet, so ackMask
        // bit i refers to message i directly. Treating that case as "nothing confirmed" would
        // ignore the very first selective ACK and force an unnecessary RTO retransmission.
        if (_pending.Count == 0)
        {
            return;
        }

        List<ulong>? confirmed = null;
        foreach (var sequence in _pending.Keys)
        {
            if (IsConfirmed(ackBase, ackMask, sequence))
            {
                (confirmed ??= []).Add(sequence);
            }
        }

        if (confirmed is null)
        {
            return;
        }

        foreach (var sequence in confirmed)
        {
            RemoveIfConfirmed(sequence);
        }
    }

    /// <summary>
    /// Whether this acknowledgement covers <paramref name="sequence"/>. The sentinel ackBase
    /// shifts the meaning of every mask bit down by one.
    /// </summary>
    private static bool IsConfirmed(ulong ackBase, ulong ackMask, ulong sequence)
    {
        if (ackBase == ulong.MaxValue)
        {
            return sequence < WindowSize && (ackMask & (1UL << (int)sequence)) != 0;
        }

        if (sequence == ackBase)
        {
            return true;
        }

        if (sequence > ackBase)
        {
            ulong ahead = sequence - ackBase;
            return ahead <= WindowSize && (ackMask & (1UL << (int)(ahead - 1))) != 0;
        }

        ulong behind = ackBase - sequence;
        return behind <= WindowSize && (ackMask & (1UL << (int)(behind - 1))) != 0;
    }

    private void RemoveIfConfirmed(ulong sequence)
    {
        if (!_pending.TryGetValue(sequence, out var item))
        {
            return;
        }

        _pending.Remove(sequence);
        // Only a never-retransmitted message yields an unambiguous RTT sample (Karn's rule).
        if (!item.Retransmitted)
        {
            long now = _nowMicroseconds();
            SampleRtt(now - item.FirstSentMicroseconds);
        }
    }

    private void FastRetransmit(ulong ackBase, ulong ackMask)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        // Highest message the peer has seen. A hole below it is missing, not merely slow.
        ulong highestSeen = ackBase == ulong.MaxValue ? 0 : ackBase;
        for (int i = WindowSize - 1; i >= 0; i--)
        {
            if ((ackMask & (1UL << i)) != 0)
            {
                ulong candidate = ackBase == ulong.MaxValue ? (ulong)i : ackBase + 1 + (ulong)i;
                if (candidate > highestSeen)
                {
                    highestSeen = candidate;
                }

                break;
            }
        }

        if (highestSeen == 0)
        {
            return;
        }

        long now = _nowMicroseconds();
        foreach (var item in _pending.Values)
        {
            if (item.Sequence < highestSeen && !item.FastRetransmitted && !IsConfirmed(ackBase, ackMask, item.Sequence))
            {
                item.FastRetransmitted = true;
                item.Attempts++;
                item.LastSentMicroseconds = now;
                item.Retransmitted = true;
                _sendFrame(ControlCodec.EncodeData(item.Sequence, _ackBase, _ackMask, item.EventType, item.Body));
            }
        }
    }

    private void OnData(ControlFrameMessage message)
    {
        ApplyAcknowledgement(message.AckBase, message.AckMask);

        bool isNew = MarkReceived(message.Sequence);
        if (isNew)
        {
            _outOfOrder[message.Sequence] = (message.EventType, message.Body.ToArray());
            DeliverContiguous();
        }

        // Always acknowledge, including duplicates: the peer may only be missing our ACK.
        _sendFrame(ControlCodec.EncodeAck(_ackBase, _ackMask));
    }

    private bool MarkReceived(ulong sequence)
    {
        if (_ackBase == ulong.MaxValue)
        {
            ulong distance = sequence + 1; // overflows to zero only for ulong.MaxValue
            if (distance == 0 || distance > WindowSize)
            {
                return false;
            }

            ulong bit = 1UL << (int)(distance - 1);
            if ((_ackMask & bit) != 0)
            {
                return false;
            }

            _ackMask |= bit;
            return true;
        }

        if (sequence > _ackBase)
        {
            ulong distance = sequence - _ackBase;
            if (distance > WindowSize)
            {
                return false;
            }

            ulong bit = 1UL << (int)(distance - 1);
            if ((_ackMask & bit) != 0)
            {
                return false;
            }

            _ackMask |= bit;
            return true;
        }

        if (sequence == _ackBase)
        {
            return false;
        }

        ulong behind = _ackBase - sequence;
        if (behind > WindowSize)
        {
            return false;
        }

        ulong oldBit = 1UL << (int)(behind - 1);
        if ((_ackMask & oldBit) != 0)
        {
            return false;
        }

        _ackMask |= oldBit;
        return true;
    }

    private void DeliverContiguous()
    {
        while ((_ackMask & 1UL) != 0)
        {
            ulong confirmed = _ackBase == ulong.MaxValue ? 0 : _ackBase + 1;
            _ackBase = confirmed;
            _ackMask >>= 1;

            if (_outOfOrder.Remove(confirmed, out var item))
            {
                _onDelivered(item.Type, item.Body);
            }
        }
    }

    private void SampleRtt(long sampleMicroseconds)
    {
        if (sampleMicroseconds <= 0)
        {
            return;
        }

        if (_srtt < 0)
        {
            _srtt = sampleMicroseconds;
            _rttVar = sampleMicroseconds / 2.0;
        }
        else
        {
            double delta = sampleMicroseconds - _srtt;
            _srtt += delta / 8.0;
            _rttVar += (Math.Abs(delta) - _rttVar) / 4.0;
        }

        double rto = _srtt + Math.Max(4 * _rttVar, 1000);
        _rto = (long)Math.Clamp(rto, MinRtoMicroseconds, MaxRtoMicroseconds);
    }

    private sealed class PendingSend(ulong sequence, ControlEventType eventType, byte[] body, long now)
    {
        public ulong Sequence { get; } = sequence;
        public ControlEventType EventType { get; } = eventType;
        public byte[] Body { get; } = body;
        public long FirstSentMicroseconds { get; } = now;
        public long LastSentMicroseconds { get; set; } = now;
        public int Attempts { get; set; }
        public bool Retransmitted { get; set; }
        public bool FastRetransmitted { get; set; }
    }
}