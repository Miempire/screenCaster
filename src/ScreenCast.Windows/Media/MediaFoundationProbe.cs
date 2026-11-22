using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace ScreenCast.Windows.Media;

/// <summary>One H.264 encoder transform registered with Media Foundation.</summary>
public sealed record MftInfo(string FriendlyName, bool Async, bool D3D11Aware);

/// <summary>
/// Runtime probing of what this machine actually offers: the registered H.264 encoder MFTs and
/// whether Media Foundation can wrap a D3D11 texture into an IMFMediaBuffer here.
/// </summary>
public static class MediaFoundationProbe
{
    public const uint MftEnumSyncMft = 0x00000001;
    public const uint MftEnumAsyncMft = 0x00000002;
    public const uint MftEnumHardware = 0x00000004;
    public const uint MftEnumSortAndFilter = 0x00000040;
    public const uint MftEnumAll = 0x0000003F;

    private static readonly Guid IidIDxgiSurface = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
    private static readonly Guid MfVideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");

    /// <summary>Enumerates H.264 encoder MFTs with the given MFT_ENUM_FLAG combination.</summary>
    public static IReadOnlyList<MftInfo> EnumerateH264EncoderMfts(uint flags)
    {
        MediaFactory.MFStartup(false).CheckError();
        try
        {
            var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = MfVideoFormatNv12 };
            var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = MfVideoFormatH264 };
            MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, input, output, out var activates, out var count);
            var result = new List<MftInfo>((int)count);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var pointer = Marshal.ReadIntPtr(activates, i * IntPtr.Size);
                    if (pointer == IntPtr.Zero)
                        continue;
                    using var activate = new IMFActivate(pointer);
                    var name = TryGetName(activate);
                    var async = TryGetUInt32(activate, TransformAttributeKeys.TransformAsync);
                    var d3d11Aware = TryGetUInt32(activate, TransformAttributeKeys.D3D11Aware);
                    result.Add(new MftInfo(name, async, d3d11Aware));
                }
            }
            finally
            {
                if (activates != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(activates);
            }

            return result;
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    /// <summary>
    /// Proves whether <c>MFCreateDXGISurfaceBuffer</c> accepts a texture created by this process.
    /// This is the route a D3D11-aware hardware MFT would need; the software MFT cannot consume it.
    /// </summary>
    public static bool TryCreateSurfaceBuffer(ID3D11Device device, out string detail)
    {
        ArgumentNullException.ThrowIfNull(device);
        MediaFactory.MFStartup(false).CheckError();
        try
        {
            var attempts = new List<string>();
            (string Name, BindFlags Bind, ResourceOptionFlags Misc)[] variants =
            [
                ("RT|SR + Shared", BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceOptionFlags.Shared),
                ("RT|SR + None", BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceOptionFlags.None),
                ("RT + Shared", BindFlags.RenderTarget, ResourceOptionFlags.Shared),
                ("RT|SR + SharedKeyedMutex", BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceOptionFlags.SharedKeyedMutex),
            ];

            foreach (var variant in variants)
            {
                try
                {
                    using var texture = device.CreateTexture2D(new Texture2DDescription
                    {
                        Format = Format.B8G8R8A8_UNorm,
                        Width = 64,
                        Height = 64,
                        ArraySize = 1,
                        MipLevels = 1,
                        BindFlags = variant.Bind,
                        Usage = ResourceUsage.Default,
                        CPUAccessFlags = CpuAccessFlags.None,
                        SampleDescription = new SampleDescription(1, 0),
                        MiscFlags = variant.Misc,
                    });

                    using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(IidIDxgiSurface, texture, 0, false);
                    if (buffer is not null)
                    {
                        detail = $"{variant.Name}: 成功（MaxLength={buffer.MaxLength} 字节）";
                        return true;
                    }

                    attempts.Add($"{variant.Name}: 返回 null");
                }
                catch (SharpGenException ex)
                {
                    attempts.Add($"{variant.Name}: 0x{ex.HResult:X8}");
                }
            }

            detail = string.Join("; ", attempts);
            return false;
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    private static string TryGetName(IMFActivate activate)
    {
        try
        {
            return activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
        }
        catch (SharpGenException)
        {
            return "(未提供 friendly name)";
        }
    }

    private static bool TryGetUInt32(IMFActivate activate, Guid key)
    {
        var result = activate.GetUInt32(key, out var value);
        return result.Success && value != 0;
    }
}
