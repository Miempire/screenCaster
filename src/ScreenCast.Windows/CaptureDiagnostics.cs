using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace ScreenCast.Windows;

public sealed record DisplayInfo(uint AdapterIndex, uint OutputIndex, long AdapterLuid,
    string AdapterName, string DeviceName, int Left, int Top, int Width, int Height, string Rotation)
{
    public override string ToString() => $"{DeviceName} — {Width}×{Height} @ ({Left},{Top}), {Rotation} — {AdapterName}";
}

public sealed record FrameDiagnostic(string Display, uint Width, uint Height, string Format,
    uint AccumulatedFrames, uint DirtyRectCount, uint MoveRectCount, long LastPresentTime);

public static class CaptureDiagnostics
{
    private const int DxgiNotFound = unchecked((int)0x887A0002);
    private const int DxgiWaitTimeout = unchecked((int)0x887A0027);

    public static IReadOnlyList<DisplayInfo> EnumerateDisplays()
    {
        var displays = new List<DisplayInfo>();
        using var factory = CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; ; a++)
        {
            var result = factory.EnumAdapters1(a, out var adapter);
            if (result.Code == DxgiNotFound) break;
            result.CheckError();
            using (adapter)
            {
                var description = adapter.Description1;
                if ((description.Flags & AdapterFlags.Software) != 0) continue;
                for (uint o = 0; ; o++)
                {
                    result = adapter.EnumOutputs(o, out var output);
                    if (result.Code == DxgiNotFound) break;
                    result.CheckError();
                    using (output)
                    {
                        var d = output.Description;
                        if (!d.AttachedToDesktop) continue;
                        var r = d.DesktopCoordinates;
                        displays.Add(new(a, o, description.Luid, description.Description, d.DeviceName,
                            r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, d.Rotation.ToString()));
                    }
                }
            }
        }
        if (displays.Count == 0)
            throw new InvalidOperationException("未检测到连接桌面的硬件 GPU 显示输出；无 GPU、无交互桌面或远程会话可能导致此结果。");
        return displays;
    }

    // Every COM object and acquired frame belongs to this synchronous operation on one worker thread.
    // Only value metadata leaves this scope: no texture is used after ReleaseFrame.
    public static FrameDiagnostic CaptureOneFrame(DisplayInfo display)
    {
        using var factory = CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1(display.AdapterIndex, out var adapter).CheckError();
        using (adapter)
        {
            if (adapter.Description1.Luid != display.AdapterLuid)
                throw new InvalidOperationException("显示适配器已改变，请刷新显示器列表。");
            adapter.EnumOutputs(display.OutputIndex, out var output).CheckError();
            using (output)
            {
                if (output.Description.DeviceName != display.DeviceName)
                    throw new InvalidOperationException("显示输出已改变，请刷新显示器列表。");
                D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device,
                    out ID3D11DeviceContext context).CheckError();
                using (device)
                using (context)
                using (var output1 = output.QueryInterface<IDXGIOutput1>())
                using (var duplication = output1.DuplicateOutput(device))
                {
                    var result = duplication.AcquireNextFrame(1500, out var info, out var resource);
                    if (result.Code == DxgiWaitTimeout)
                        throw new TimeoutException("DDA 等待 1500ms 未取得帧，未报告采集成功；请更新桌面画面后重试。");
                    result.CheckError();
                    try
                    {
                        using (resource)
                        using (var texture = resource.QueryInterface<ID3D11Texture2D>())
                        {
                            // TotalMetadataBufferSize bounds either individual rectangle buffer.
                            const uint maxMetadataBytes = 16 * 1024 * 1024;
                            if (info.TotalMetadataBufferSize > maxMetadataBytes)
                                throw new InvalidOperationException("DDA 元数据超过 16 MiB 诊断上限。");
                            uint dirtyBytes = 0, moveBytes = 0;
                            if (info.TotalMetadataBufferSize > 0)
                            {
                                int dirtySize = Marshal.SizeOf<RawRect>();
                                int moveSize = Marshal.SizeOf<OutduplMoveRect>();
                                var dirty = new RawRect[(info.TotalMetadataBufferSize + dirtySize - 1) / dirtySize];
                                var moves = new OutduplMoveRect[(info.TotalMetadataBufferSize + moveSize - 1) / moveSize];
                                duplication.GetFrameDirtyRects((uint)(dirty.Length * dirtySize), dirty, out dirtyBytes).CheckError();
                                duplication.GetFrameMoveRects((uint)(moves.Length * moveSize), moves, out moveBytes).CheckError();
                            }
                            var td = texture.Description;
                            return new(display.DeviceName, td.Width, td.Height, td.Format.ToString(),
                                info.AccumulatedFrames, dirtyBytes / (uint)Marshal.SizeOf<RawRect>(),
                                moveBytes / (uint)Marshal.SizeOf<OutduplMoveRect>(), info.LastPresentTime);
                        }
                    }
                    finally
                    {
                        duplication.ReleaseFrame().CheckError();
                    }
                }
            }
        }
    }
}
