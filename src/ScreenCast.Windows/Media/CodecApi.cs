using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace ScreenCast.Windows.Media;

/// <summary>
/// Minimal <c>ICodecAPI</c> access for the encoder MFT. Only the value-setting subset is needed:
/// B-frame count (screen streaming wants zero to keep decode order equal to capture order) and the
/// low-latency flag. Failures are reported, never hidden.
/// </summary>
internal static class CodecApi
{
    public static readonly Guid AvEncMpvDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid AvEncCommonLowLatency = new("9d3ecd55-89e8-490a-970a-0c850dc12afb");

    private const ushort VariantTypeUInt32 = 19;
    private const ushort VariantTypeBool = 11;

    public static bool TrySetUInt32(IMFTransform transform, Guid api, uint value, out int hresult)
        => TrySet(transform, api, VariantTypeUInt32, value, out hresult, isBool: false);

    public static bool TrySetBool(IMFTransform transform, Guid api, bool value, out int hresult)
        => TrySet(transform, api, VariantTypeBool, value ? 0xFFFFu : 0u, out hresult, isBool: true);

    private static bool TrySet(IMFTransform transform, Guid api, ushort variantType, uint value, out int hresult, bool isBool)
    {
        var iid = typeof(ICodecApi).GUID;
        hresult = Marshal.QueryInterface(transform.NativePointer, ref iid, out var codecApiPointer);
        if (hresult < 0)
            return false;

        try
        {
            var codecApi = (ICodecApi)Marshal.GetObjectForIUnknown(codecApiPointer);
            var variant = isBool
                ? new Variant { Type = VariantTypeBool, BoolValue = (short)value }
                : new Variant { Type = VariantTypeUInt32, UInt32Value = value };
            var key = api;
            hresult = codecApi.SetValue(ref key, ref variant);
            return hresult >= 0;
        }
        catch (InvalidCastException)
        {
            hresult = unchecked((int)0x80004002);
            return false;
        }
        finally
        {
            Marshal.Release(codecApiPointer);
        }
    }

    [ComImport]
    [Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecApi
    {
        [PreserveSig]
        int IsSupported(ref Guid api, out short isSupported);

        [PreserveSig]
        int GetParameterRange(ref Guid api, IntPtr minValue, IntPtr maxValue, IntPtr steppingDelta);

        [PreserveSig]
        int GetParameterValues(ref Guid api, out IntPtr values, out uint valueCount);

        [PreserveSig]
        int GetDefaultValue(ref Guid api, out Variant defaultValue);

        [PreserveSig]
        int SetValue(ref Guid api, ref Variant value);

        [PreserveSig]
        int GetValue(ref Guid api, out Variant value);
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct Variant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public uint UInt32Value;

        [FieldOffset(8)]
        public short BoolValue;
    }
}
