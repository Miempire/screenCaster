using System.Net;

namespace ScreenCast.Core;

public sealed record ReceivedDatagram(ReadOnlyMemory<byte> Payload, IPEndPoint Source);
/// <summary>ReceiveAsync cancellation must stop this receive, without closing the stable media socket.</summary>
public interface IBindingTransport
{
    ValueTask SendAsync(ReadOnlyMemory<byte> payload, IPEndPoint destination, CancellationToken cancellationToken);
    ValueTask<ReceivedDatagram> ReceiveAsync(CancellationToken cancellationToken);
}
public sealed record SignalEnvelope(string Type, string RequestId, ReadOnlyMemory<byte> Utf8Body);
/// <summary>Implementations must authenticate identity and complete the specified handshake before application messages.</summary>
public interface ISignalingClient : IAsyncDisposable
{
    ValueTask ConnectAsync(CancellationToken cancellationToken);
    ValueTask SendAsync(SignalEnvelope message, CancellationToken cancellationToken);
    IAsyncEnumerable<SignalEnvelope> ReceiveAsync(CancellationToken cancellationToken);
}
public sealed record RelayAllocation(IPEndPoint RelayedEndpoint, DateTimeOffset ExpiresAt);
/// <summary>Future standard TURN implementation. Presence of this interface does not imply TURN support.</summary>
public interface ITurnClient : IAsyncDisposable
{
    ValueTask<RelayAllocation> AllocateAsync(TransportComponent component, CancellationToken cancellationToken);
    ValueTask CreatePermissionAsync(IPAddress peer, CancellationToken cancellationToken);
    ValueTask RefreshAsync(TimeSpan lifetime, CancellationToken cancellationToken);
    ValueTask SendAsync(ReadOnlyMemory<byte> encryptedDatagram, IPEndPoint peer, CancellationToken cancellationToken);
}
public sealed record CapturedFrame(int Width, int Height, int Stride, string PixelFormat, long TimestampMicroseconds, ReadOnlyMemory<byte> Pixels);
public sealed record EncodedFrame(ulong FrameId, uint ConfigurationId, bool IsKeyFrame, ReadOnlyMemory<byte> Data);
public interface IFrameCapture : IAsyncDisposable
{
    IAsyncEnumerable<CapturedFrame> CaptureAsync(CancellationToken cancellationToken);
}
public interface IVideoEncoder : IAsyncDisposable
{
    ValueTask<EncodedFrame> EncodeAsync(CapturedFrame frame, bool requestKeyFrame, CancellationToken cancellationToken);
}
