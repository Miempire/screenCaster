using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace ScreenCast.Core.Transport;

/// <summary>
/// A received datagram whose payload buffer is rented from <see cref="ArrayPool{T}"/>.
/// The consumer owns the lease and must dispose it; the payload is invalid afterwards.
/// </summary>
public sealed class DatagramLease : IDisposable
{
    private byte[]? _rented;

    public ReadOnlyMemory<byte> Payload { get; }
    public IPEndPoint Source { get; }

    internal DatagramLease(byte[] rented, int length, IPEndPoint source)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, rented.Length);
        _rented = rented;
        Payload = rented.AsMemory(0, length);
        Source = source;
    }

    public void Dispose()
    {
        var buffer = _rented;
        _rented = null;
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// A stable bound UDP socket that is reused for STUN discovery, hole punching and
/// application traffic for its whole lifetime.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one <see cref="ReceiveAsync"/> owner is allowed at a time. The socket is not
/// closed when a receive is cancelled: cancellation only aborts that one receive, which is
/// required because the port mapping discovered by STUN must survive session setup.
/// </para>
/// <para>
/// Use one instance per media component (video, control). Do not share one instance between
/// components: each mapping, keepalive deadline and TURN allocation belongs to its own socket.
/// </para>
/// </remarks>
public sealed class UdpTransport : IDisposable, IAsyncDisposable
{
    public const int MaxDatagramSize = 1500;

    private readonly Socket _socket;
    private readonly IPEndPoint _anyReceiveEndPoint;
    private readonly byte[] _receiveBuffer = new byte[MaxDatagramSize];
    private int _receiving;
    private bool _disposed;

    public UdpTransport(IPEndPoint bindEndPoint, int receiveBufferBytes = DefaultReceiveBufferBytes)
    {
        ArgumentNullException.ThrowIfNull(bindEndPoint);
        _socket = new Socket(bindEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

        // A key frame arrives as one burst of dozens of datagrams. The default receive buffer on
        // Windows is far too small for that burst, so fragments are silently dropped and the whole
        // frame is lost — which then breaks every following P frame. Enlarging the buffer is the
        // difference between "video works" and "video is a black screen".
        TrySetBufferSize(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, receiveBufferBytes);
        TrySetBufferSize(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, receiveBufferBytes);

        // Bind before the first STUN request so the discovered mapping matches this port.
        _socket.Bind(bindEndPoint);
        _anyReceiveEndPoint = new IPEndPoint(
            bindEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
    }

    /// <summary>Default socket buffer: large enough for a 1080p key frame burst.</summary>
    public const int DefaultReceiveBufferBytes = 4 * 1024 * 1024;

    private void TrySetBufferSize(SocketOptionLevel level, SocketOptionName name, int bytes)
    {
        try
        {
            _socket.SetSocketOption(level, name, bytes);
        }
        catch (SocketException)
        {
            // The platform may cap the value; the transport still works with whatever it allows.
        }
    }

    /// <summary>Buffer size the socket actually accepted, for diagnostics.</summary>
    public int ActualReceiveBufferBytes
    {
        get
        {
            try
            {
                return (int)_socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer)!;
            }
            catch (SocketException)
            {
                return 0;
            }
        }
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    public int LocalPort => LocalEndPoint.Port;

    /// <summary>Maximum payload this transport accepts per datagram.</summary>
    public int MaxPayloadSize => MaxDatagramSize;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, IPEndPoint destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (payload.Length > MaxDatagramSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Datagram exceeds transport limit.");
        }

        if (destination.AddressFamily != _socket.AddressFamily)
        {
            throw new ArgumentException("Destination address family does not match the bound socket.", nameof(destination));
        }

        await _socket.SendToAsync(payload, SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Receive one datagram. Cancellation aborts only this receive; the socket stays bound
    /// and usable, so the NAT mapping and port are preserved.
    /// </summary>
    public async ValueTask<DatagramLease> ReceiveAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _receiving, 1) != 0)
        {
            throw new InvalidOperationException("Only one ReceiveAsync owner is allowed per transport.");
        }

        try
        {
            var result = await _socket
                .ReceiveFromAsync(_receiveBuffer, SocketFlags.None, _anyReceiveEndPoint, cancellationToken)
                .ConfigureAwait(false);

            var rented = ArrayPool<byte>.Shared.Rent(Math.Max(result.ReceivedBytes, 1));
            _receiveBuffer.AsSpan(0, result.ReceivedBytes).CopyTo(rented);
            return new DatagramLease(rented, result.ReceivedBytes, (IPEndPoint)result.RemoteEndPoint);
        }
        finally
        {
            Volatile.Write(ref _receiving, 0);
        }
    }

    /// <summary>
    /// Wait for one datagram and report only its source address, leaving the datagram in the socket
    /// for the real receiver. Used by a direct host to learn its peer before a session exists.
    /// </summary>
    /// <remarks>
    /// This deliberately does not consume the datagram: the session's authenticated receive loop is
    /// the only owner allowed to pull from the socket, and stealing the first packet would break the
    /// handshake it carries.
    /// </remarks>
    public bool TryPeekSource(int timeoutMilliseconds, byte[] scratch, out IPEndPoint? source)
    {
        source = null;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeoutMilliseconds <= 0 || scratch.Length == 0)
        {
            return false;
        }

        try
        {
            if (!_socket.Poll(timeoutMilliseconds * 1000, SelectMode.SelectRead))
            {
                return false;
            }

            // MSG_PEEK leaves the datagram queued so the session can consume it.
            EndPoint remote = _anyReceiveEndPoint;
            int received = _socket.ReceiveFrom(scratch, SocketFlags.Peek, ref remote);
            if (received <= 0)
            {
                return false;
            }

            source = (IPEndPoint)remote;
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _socket.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}