using System.Buffers.Binary;

namespace ScreenCast.Core.Transport;

public enum VideoCodec : byte
{
    Unknown = 0,
    H264 = 1,
    Av1 = 2,
}

[Flags]
public enum VideoFrameFlags : byte
{
    None = 0,
    KeyFrame = 1,
    Configuration = 2,
    Discontinuity = 4,
}

/// <summary>
/// 32-byte video fragment prefix: frameId(8) captureTimestampUs(8) configId(4)
/// fragmentIndex(2) fragmentCount(2) frameLength(4) flags(1) codec(1) reserved(2),
/// followed by the H.264 Annex-B or AV1 OBU bytes of that fragment.
/// </summary>
public readonly record struct VideoFragmentHeader(
    ulong FrameId,
    ulong CaptureTimestampMicroseconds,
    uint ConfigId,
    ushort FragmentIndex,
    ushort FragmentCount,
    uint FrameLength,
    VideoFrameFlags Flags,
    VideoCodec Codec)
{
    public const int Size = 32;

    /// <summary>Largest fragment payload that fits one protected datagram.</summary>
    public const int MaxFragmentPayload = PacketHeader.MaxDatagramSize - PacketHeader.Size - Size;

    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("Fragment header buffer too short.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, FrameId);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], CaptureTimestampMicroseconds);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], ConfigId);
        BinaryPrimitives.WriteUInt16BigEndian(destination[20..], FragmentIndex);
        BinaryPrimitives.WriteUInt16BigEndian(destination[22..], FragmentCount);
        BinaryPrimitives.WriteUInt32BigEndian(destination[24..], FrameLength);
        destination[28] = (byte)Flags;
        destination[29] = (byte)Codec;
        destination[30] = 0;
        destination[31] = 0;
    }

    public static VideoFragmentHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new FormatException("Truncated video fragment header.");
        }

        return new VideoFragmentHeader(
            BinaryPrimitives.ReadUInt64BigEndian(source),
            BinaryPrimitives.ReadUInt64BigEndian(source[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[16..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[20..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[22..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[24..]),
            (VideoFrameFlags)source[28],
            (VideoCodec)source[29]);
    }
}

public sealed record AssembledVideoFrame(
    ulong FrameId,
    ulong CaptureTimestampMicroseconds,
    uint ConfigId,
    VideoFrameFlags Flags,
    VideoCodec Codec,
    byte[] Data);

/// <summary>
/// Reassembles fragment datagrams into whole frames with bounded memory. A partial frame is
/// never handed to the decoder: video tolerates loss by dropping the frame and asking for a new
/// key frame, never by retransmitting fragments.
/// </summary>
public sealed class VideoAssembler
{
    public const int MaxConcurrentFrames = 4;
    public const int MaxFrameBytes = 8 * 1024 * 1024;
    public const int MaxFragmentsPerFrame = 8192;

    private readonly Dictionary<ulong, PartialFrame> _partial = [];
    private readonly Func<long> _nowMicroseconds;
    private readonly long _frameLifetimeMicroseconds;
    private int _bufferedBytes;

    public VideoAssembler(Func<long>? nowMicroseconds = null, long frameLifetimeMicroseconds = 500_000)
    {
        _nowMicroseconds = nowMicroseconds ?? DefaultClock;
        _frameLifetimeMicroseconds = frameLifetimeMicroseconds;
    }

    public int BufferedBytes => _bufferedBytes;

    public int PartialFrameCount => _partial.Count;

    public long DroppedFragments { get; private set; }

    public long DroppedFrames { get; private set; }

    private static long DefaultClock() =>
        System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Feed one authenticated fragment. Returns a complete frame when this fragment finished it,
    /// otherwise null. Expired partial frames are discarded first so memory stays bounded.
    /// </summary>
    public AssembledVideoFrame? Push(ReadOnlySpan<byte> plaintext)
    {
        // Sweep before parsing so a malformed datagram still ages out stale partial frames.
        SweepExpired();

        VideoFragmentHeader header;
        try
        {
            header = VideoFragmentHeader.Parse(plaintext);
        }
        catch (FormatException)
        {
            DroppedFragments++;
            return null;
        }

        if (header.FragmentCount == 0
            || header.FragmentIndex >= header.FragmentCount
            || header.FragmentCount > MaxFragmentsPerFrame
            || header.FrameLength == 0
            || header.FrameLength > MaxFrameBytes)
        {
            DroppedFragments++;
            return null;
        }

        ReadOnlySpan<byte> payload = plaintext[VideoFragmentHeader.Size..];
        if (payload.Length > VideoFragmentHeader.MaxFragmentPayload)
        {
            DroppedFragments++;
            return null;
        }

        if (!_partial.TryGetValue(header.FrameId, out var frame))
        {
            if (_partial.Count >= MaxConcurrentFrames)
            {
                // Evict the oldest partial frame rather than growing without bound.
                EvictOldest();
            }

            frame = new PartialFrame(header, _nowMicroseconds());
            _partial[header.FrameId] = frame;
        }
        else if (frame.Header.FragmentCount != header.FragmentCount
                 || frame.Header.FrameLength != header.FrameLength
                 || frame.Header.Codec != header.Codec)
        {
            // Conflicting metadata for the same frame id: treat the whole frame as lost.
            ReleaseFrame(frame);
            _partial.Remove(header.FrameId);
            DroppedFrames++;
            DroppedFragments++;
            return null;
        }

        if (frame.Fragments[header.FragmentIndex] is not null)
        {
            // Duplicate fragment: ignore, it adds no information.
            return null;
        }

        if (_bufferedBytes + payload.Length > MaxConcurrentFrames * (long)MaxFrameBytes)
        {
            ReleaseFrame(frame);
            _partial.Remove(header.FrameId);
            DroppedFrames++;
            return null;
        }

        byte[] copy = payload.ToArray();
        frame.Fragments[header.FragmentIndex] = copy;
        frame.Received++;
        _bufferedBytes += copy.Length;

        if (frame.Received < header.FragmentCount)
        {
            return null;
        }

        _partial.Remove(header.FrameId);
        _bufferedBytes -= frame.ReceivedBytes;
        return Complete(frame);
    }

    private static AssembledVideoFrame Complete(PartialFrame frame)
    {
        int total = 0;
        foreach (var fragment in frame.Fragments)
        {
            total += fragment?.Length ?? 0;
        }

        byte[] data = new byte[total];
        int offset = 0;
        foreach (var fragment in frame.Fragments)
        {
            if (fragment is null)
            {
                continue;
            }

            fragment.CopyTo(data, offset);
            offset += fragment.Length;
        }

        return new AssembledVideoFrame(frame.Header.FrameId, frame.Header.CaptureTimestampMicroseconds,
            frame.Header.ConfigId, frame.Header.Flags, frame.Header.Codec, data);
    }

    private void SweepExpired()
    {
        if (_partial.Count == 0)
        {
            return;
        }

        long now = _nowMicroseconds();
        List<ulong>? expired = null;
        foreach (var pair in _partial)
        {
            if (now - pair.Value.CreatedAtMicroseconds >= _frameLifetimeMicroseconds)
            {
                (expired ??= []).Add(pair.Key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var id in expired)
        {
            if (_partial.Remove(id, out var frame))
            {
                ReleaseFrame(frame);
                DroppedFrames++;
            }
        }
    }

    private void EvictOldest()
    {
        ulong oldestId = 0;
        long oldestTime = long.MaxValue;
        foreach (var pair in _partial)
        {
            if (pair.Value.CreatedAtMicroseconds < oldestTime)
            {
                oldestTime = pair.Value.CreatedAtMicroseconds;
                oldestId = pair.Key;
            }
        }

        if (_partial.Remove(oldestId, out var oldest))
        {
            ReleaseFrame(oldest);
            DroppedFrames++;
        }
    }

    private void ReleaseFrame(PartialFrame frame)
    {
        _bufferedBytes -= frame.ReceivedBytes;
        if (_bufferedBytes < 0)
        {
            _bufferedBytes = 0;
        }
    }

    private sealed class PartialFrame
    {
        public PartialFrame(VideoFragmentHeader header, long createdAtMicroseconds)
        {
            Header = header;
            CreatedAtMicroseconds = createdAtMicroseconds;
            Fragments = new byte[]?[header.FragmentCount];
        }

        public VideoFragmentHeader Header { get; }
        public long CreatedAtMicroseconds { get; }
        public byte[]?[] Fragments { get; }
        public int Received { get; set; }

        public int ReceivedBytes
        {
            get
            {
                int total = 0;
                foreach (var fragment in Fragments)
                {
                    total += fragment?.Length ?? 0;
                }

                return total;
            }
        }
    }
}

/// <summary>
/// Splits one encoded frame into fragment datagrams. The splitter is stateless: the caller owns
/// the frame id, configuration id and flags, so a new key frame or configuration change is
/// always explicit.
/// </summary>
public static class VideoFragmenter
{
    public static int FragmentCountFor(int frameLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameLength);
        int max = VideoFragmentHeader.MaxFragmentPayload;
        return (frameLength + max - 1) / max;
    }

    /// <summary>Write the plaintext of fragment <paramref name="index"/> into <paramref name="destination"/>.</summary>
    public static int WriteFragment(ReadOnlySpan<byte> frameData, ulong frameId,
        ulong captureTimestampMicroseconds, uint configId, VideoFrameFlags flags, VideoCodec codec,
        int index, Span<byte> destination)
    {
        int max = VideoFragmentHeader.MaxFragmentPayload;
        int count = FragmentCountFor(frameData.Length);
        if (index < 0 || index >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        int start = index * max;
        int length = Math.Min(max, frameData.Length - start);
        if (destination.Length < VideoFragmentHeader.Size + length)
        {
            throw new ArgumentException("Fragment destination too short.", nameof(destination));
        }

        new VideoFragmentHeader(frameId, captureTimestampMicroseconds, configId,
            (ushort)index, (ushort)count, (uint)frameData.Length, flags, codec)
            .Write(destination);
        frameData.Slice(start, length).CopyTo(destination[VideoFragmentHeader.Size..]);
        return VideoFragmentHeader.Size + length;
    }
}