using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice;
using Vortice.MediaFoundation;

namespace ScreenCast.Windows.Media;

/// <summary>Fixed encoder parameters. Resolution scaling happens before the encoder (see Nv12Converter).</summary>
public sealed record H264EncoderSettings(int Width, int Height, int FramesPerSecond, int BitrateBitsPerSecond, int KeyframeInterval)
{
    public static H264EncoderSettings Default720p30 { get; } = new(1280, 720, 30, 4_000_000, 30);

    public long FrameDurationTicks => 10_000_000 / FramesPerSecond;

    public int Nv12FrameSize => Width * Height * 3 / 2;
}

/// <summary>
/// H.264 encoder wrapping the in-box Media Foundation transform <c>CLSID_CMSH264EncoderMFT</c>.
/// Input is NV12 system memory: the software MFT does not accept DXGI surface buffers, which is why
/// the captured frame is converted on the CPU. MediaFoundationProbe records whether the D3D11 route
/// works on this machine. Output is normalized to Annex-B with per-sample timestamps and keyframe flags.
/// </summary>
public sealed class H264Encoder : IDisposable
{
    private const int ClsCtxInprocServer = 0x1;
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfENoMoreTypes = unchecked((int)0xC00D36B5);
    private const uint H264ProfileBaseline = 66;
    private const uint H264ProfileMain = 77;
    private const uint H264Level41 = 41;

    private static readonly Guid ClsidCmsH264EncoderMft = new("6CA50344-051A-4DED-9779-A43305165E35");
    private static readonly Guid IidImfTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");

    private readonly H264EncoderSettings _settings;
    private readonly IMFTransform _transform;
    private IMFMediaType? _outputType;
    private byte[]? _sequenceHeader;
    private int _inputAlignment;
    private int _outputBufferSize;
    private bool _streamStarted;
    private bool _mfStarted;
    private bool _disposed;

    public H264Encoder(H264EncoderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Width <= 0 || settings.Height <= 0 || (settings.Width & 1) != 0 || (settings.Height & 1) != 0)
            throw new ArgumentException("编码宽高必须是大于 0 的偶数。", nameof(settings));
        if (settings.FramesPerSecond is <= 0 or > 240)
            throw new ArgumentException("帧率必须在 1..240。", nameof(settings));
        if (settings.BitrateBitsPerSecond <= 0)
            throw new ArgumentException("目标码率必须大于 0。", nameof(settings));

        _settings = settings;
        MediaFactory.MFStartup(false).CheckError();
        _mfStarted = true;
        _transform = CreateEncoderTransform();
        ConfigureMediaTypes();
        ConfigureLatency();
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _streamStarted = true;
    }

    private void ConfigureLatency()
    {
        var log = AttemptLog.ToList();
        var bFrames = CodecApi.TrySetUInt32(_transform, CodecApi.AvEncMpvDefaultBPictureCount, 0, out var bFrameHr);
        log.Add(bFrames ? "ICodecAPI B帧=0: OK" : $"ICodecAPI B帧=0: 0x{bFrameHr:X8}");
        var lowLatency = CodecApi.TrySetBool(_transform, CodecApi.AvEncCommonLowLatency, true, out var latencyHr);
        log.Add(lowLatency ? "ICodecAPI 低延迟=1: OK" : $"ICodecAPI 低延迟=1: 0x{latencyHr:X8}");
        AttemptLog = log;
    }

    public string EncoderName => "CMSH264EncoderMFT (Windows 内置 H.264 MFT, 软件)";

    public H264EncoderSettings Settings => _settings;

    /// <summary>True when the MFT allocates output samples itself (no client buffer needed).</summary>
    public bool OutputProvidesSamples { get; private set; }

    public int SequenceHeaderLength => _sequenceHeader?.Length ?? 0;

    /// <summary>Negotiation log: which media-type attempts the MFT accepted or rejected, with HRESULTs.</summary>
    public IReadOnlyList<string> AttemptLog { get; private set; } = [];

    /// <summary>Encodes one NV12 frame and returns whatever access units the MFT is ready to emit.</summary>
    public IReadOnlyList<EncodedSample> Feed(ReadOnlySpan<byte> nv12Frame, long timestampUs, bool requestKeyFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nv12Frame.Length != _settings.Nv12FrameSize)
            throw new ArgumentException($"NV12 帧应为 {_settings.Nv12FrameSize} 字节，实际 {nv12Frame.Length} 字节。", nameof(nv12Frame));
        if (timestampUs < 0)
            throw new ArgumentOutOfRangeException(nameof(timestampUs));

        using var sample = MediaFactory.MFCreateSample();
        using var buffer = MediaFactory.MFCreateMemoryBuffer(nv12Frame.Length + Math.Max(0, _inputAlignment));
        buffer.Lock(out var destination, out _, out _);
        try
        {
            unsafe
            {
                nv12Frame.CopyTo(new Span<byte>((void*)destination, nv12Frame.Length));
            }
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = nv12Frame.Length;
        sample.AddBuffer(buffer);
        sample.SampleTime = timestampUs * 10;
        sample.SampleDuration = _settings.FrameDurationTicks;
        if (requestKeyFrame)
            sample.Set(SampleAttributeKeys.CleanPoint, true).CheckError();

        _transform.ProcessInput(0, sample, 0);
        return CollectSamples();
    }

    /// <summary>Signals end of stream and returns the access units still buffered in the MFT.</summary>
    public IReadOnlyList<EncodedSample> Drain()
    {
        if (_disposed)
            return [];
        try
        {
            _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        }
        catch (SharpGenException)
        {
            // Some MFT builds reject drain without an active input stream; nothing is buffered then.
            return [];
        }

        return CollectSamples();
    }

    private IReadOnlyList<EncodedSample> CollectSamples()
    {
        var samples = new List<EncodedSample>();
        while (true)
        {
            var output = new OutputDataBuffer { StreamID = 0, Sample = null, Status = 0, Events = null };
            IMFSample? provided = null;
            if (!OutputProvidesSamples)
            {
                provided = MediaFactory.MFCreateSample();
                using var buffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(_outputBufferSize, 1));
                provided.AddBuffer(buffer);
            }

            output.Sample = provided;
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            if (result.Success)
            {
                var produced = output.Sample;
                if (produced is null)
                    break;
                try
                {
                    samples.Add(ReadSample(produced));
                }
                finally
                {
                    produced.Dispose();
                }

                continue;
            }

            if (result.Code == MfETransformNeedMoreInput)
            {
                output.Sample?.Dispose();
                break;
            }

            if (result.Code == MfETransformStreamChange)
            {
                output.Sample?.Dispose();
                RefreshOutputType();
                continue;
            }

            output.Sample?.Dispose();
            result.CheckError();
        }

        return samples;
    }

    private EncodedSample ReadSample(IMFSample sample)
    {
        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var pointer, out var maxLength, out var currentLength);
        byte[] data;
        try
        {
            if (currentLength <= 0 || currentLength > maxLength)
                throw new InvalidOperationException($"编码输出样本长度异常：{currentLength}/{maxLength}。");
            data = new byte[currentLength];
            Marshal.Copy(pointer, data, 0, currentLength);
        }
        finally
        {
            contiguous.Unlock();
        }

        var timestampTicks = sample.SampleTime;
        var cleanPoint = sample.GetUInt32(SampleAttributeKeys.CleanPoint, out var cleanPointValue);
        var isCleanPoint = cleanPoint.Success && cleanPointValue != 0;

        var annexB = H264Bitstream.NormalizeToAnnexB(data, _sequenceHeader ?? [], out var converted);
        var units = H264Bitstream.ParseAnnexB(annexB);
        var hasIdr = units.Any(unit => unit.Type == H264NalUnitType.IdrSlice);
        return new EncodedSample(
            annexB,
            timestampTicks >= 0 ? timestampTicks / 10 : -1,
            isCleanPoint || hasIdr,
            converted,
            units);
    }

    private void ConfigureMediaTypes()
    {
        using var input = BuildInputType();
        var log = new List<string>();

        var available = DescribeAvailableOutputTypes(log);

        // Encoders normally require the output type first, and accept a caller-built type rather than
        // one returned by GetOutputAvailableType. Several attribute combinations are attempted.
        foreach (var (name, output) in BuildOutputTypeCandidates())
        {
            try
            {
                _transform.SetOutputType(0, output, 0);
            }
            catch (SharpGenException ex)
            {
                log.Add($"SetOutputType[{name}]: 0x{ex.HResult:X8} {ex.Message}");
                output.Dispose();
                continue;
            }

            try
            {
                _transform.SetInputType(0, input, 0);
                log.Add($"SetInputType[{name}]: OK");
                _outputType = output;
                break;
            }
            catch (SharpGenException ex)
            {
                log.Add($"SetInputType[{name}]: 0x{ex.HResult:X8} {ex.Message}");
                output.Dispose();
            }
        }

        if (_outputType is null && available is not null)
        {
            // Fallback: the MFT enumerated its own output types but wants the input type first.
            try
            {
                _transform.SetInputType(0, input, 0);
                foreach (var candidate in available)
                {
                    try
                    {
                        _transform.SetOutputType(0, candidate, 0);
                        _outputType = candidate;
                        log.Add("SetOutputType[available]: OK");
                        break;
                    }
                    catch (SharpGenException ex)
                    {
                        log.Add($"SetOutputType[available]: 0x{ex.HResult:X8} {ex.Message}");
                    }
                }
            }
            catch (SharpGenException ex)
            {
                log.Add($"SetInputType[available]: 0x{ex.HResult:X8} {ex.Message}");
            }

            foreach (var candidate in available)
                candidate.Dispose();
            available.Clear();
        }

        if (_outputType is null)
            throw new InvalidOperationException($"配置 H.264 编码器媒体类型失败：{string.Join(" | ", log)}");

        var inputInfo = _transform.GetInputStreamInfo(0);
        _inputAlignment = Math.Max(0, inputInfo.Alignment);
        var outputInfo = _transform.GetOutputStreamInfo(0);
        OutputProvidesSamples = (outputInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        _outputBufferSize = outputInfo.Size;
        _sequenceHeader = TryReadSequenceHeader(_outputType);

        _transform.GetStreamCount(out var inputs, out var outputs);
        if (inputs < 1 || outputs < 1)
            throw new InvalidOperationException($"H.264 MFT 流数量异常：输入 {inputs}，输出 {outputs}。");

        AttemptLog = log;
    }

    private List<IMFMediaType>? DescribeAvailableOutputTypes(List<string> log)
    {
        var types = new List<IMFMediaType>();
        for (var index = 0; ; index++)
        {
            IMFMediaType type;
            try
            {
                type = _transform.GetOutputAvailableType(0, index);
            }
            catch (SharpGenException ex) when (ex.HResult == MfENoMoreTypes)
            {
                log.Add($"GetOutputAvailableType: MF_E_NO_MORE_TYPES，共 {types.Count} 个。");
                break;
            }
            catch (SharpGenException ex)
            {
                log.Add($"GetOutputAvailableType[{index}]: 0x{ex.HResult:X8}");
                break;
            }

            types.Add(type);
        }

        log.Add($"GetOutputAvailableType 返回 {types.Count} 个类型。");
        return types;
    }

    private IMFMediaType BuildInputType()
    {
        var input = MediaFactory.MFCreateMediaType();
        try
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatNv12).CheckError();
            input.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)_settings.Width, (uint)_settings.Height)).CheckError();
            input.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)_settings.FramesPerSecond, 1)).CheckError();
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1)).CheckError();
            input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
            input.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_settings.BitrateBitsPerSecond).CheckError();
            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    private IEnumerable<(string Name, IMFMediaType Type)> BuildOutputTypeCandidates()
    {
        // Baseline first: its profile forbids B-frames, which keeps decode order equal to capture order
        // (the MFT's ICodecAPI surface is missing, so CODECAPI_AVEncMPVDefaultBPictureCount is not available).
        yield return ("frame-info+baseline", CreateOutputType(withFrameInfo: true, OutputProfile.Baseline));
        yield return ("frame-info", CreateOutputType(withFrameInfo: true, OutputProfile.Unspecified));
        yield return ("frame-info+main", CreateOutputType(withFrameInfo: true, OutputProfile.Main));
        yield return ("minimal", CreateOutputType(withFrameInfo: false, OutputProfile.Unspecified));
    }

    private IMFMediaType CreateOutputType(bool withFrameInfo, OutputProfile profile)
    {
        var type = MediaFactory.MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatH264).CheckError();
            type.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_settings.BitrateBitsPerSecond).CheckError();
            type.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)Math.Max(1, _settings.KeyframeInterval)).CheckError();
            if (withFrameInfo)
            {
                type.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)_settings.Width, (uint)_settings.Height)).CheckError();
                type.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)_settings.FramesPerSecond, 1)).CheckError();
                type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1)).CheckError();
                type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
            }

            if (profile != OutputProfile.Unspecified)
            {
                type.Set(MediaTypeAttributeKeys.Mpeg2Profile, profile == OutputProfile.Baseline ? H264ProfileBaseline : H264ProfileMain).CheckError();
                type.Set(MediaTypeAttributeKeys.Mpeg2Level, H264Level41).CheckError();
            }

            return type;
        }
        catch
        {
            type.Dispose();
            throw;
        }
    }

    private enum OutputProfile
    {
        Unspecified,
        Baseline,
        Main,
    }

    private static bool IsH264Video(IMFMediaType type)
    {
        if (!type.GetGUID(MediaTypeAttributeKeys.MajorType, out var major).Success || major != MediaTypeGuids.Video)
            return false;
        return type.GetGUID(MediaTypeAttributeKeys.Subtype, out var subtype).Success && subtype == VideoFormatH264;
    }

    private static byte[]? TryReadSequenceHeader(IMFMediaType type)
    {
        var size = type.GetBlobSize(MediaTypeAttributeKeys.MpegSequenceHeader, out var blobSize);
        if (!size.Success || blobSize == 0)
            return null;
        return type.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
    }

    private void RefreshOutputType()
    {
        _outputType?.Dispose();
        _outputType = null;

        // After MF_E_TRANSFORM_STREAM_CHANGE the MFT expects a fresh output type; the input stays as is.
        foreach (var candidate in BuildOutputTypeCandidates().Select(entry => entry.Type))
        {
            try
            {
                _transform.SetOutputType(0, candidate, 0);
                _outputType = candidate;
                break;
            }
            catch (SharpGenException)
            {
                candidate.Dispose();
            }
        }

        if (_outputType is null)
            throw new InvalidOperationException("MF_E_TRANSFORM_STREAM_CHANGE 之后重新设置输出类型失败。");

        _sequenceHeader = TryReadSequenceHeader(_outputType);
        var info = _transform.GetOutputStreamInfo(0);
        OutputProvidesSamples = (info.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        _outputBufferSize = info.Size;
    }

    private static IMFTransform CreateEncoderTransform()
    {
        var clsid = ClsidCmsH264EncoderMft;
        var iid = IidImfTransform;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref iid, out var pointer);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
        return new IMFTransform(pointer);
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_streamStarted)
        {
            try
            {
                _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            }
            catch (SharpGenException)
            {
                // Shutdown must not mask the failure that led here.
            }

            _streamStarted = false;
        }

        _outputType?.Dispose();
        _outputType = null;
        _transform.Dispose();

        if (_mfStarted)
        {
            _mfStarted = false;
            MediaFactory.MFShutdown();
        }
    }
}
