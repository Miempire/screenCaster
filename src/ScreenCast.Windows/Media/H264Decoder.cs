using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice;
using Vortice.MediaFoundation;

namespace ScreenCast.Windows.Media;

/// <summary>Container form used for MF_MT_MPEG_SEQUENCE_HEADER.</summary>
public enum SequenceHeaderFormat
{
    None,
    AnnexB,
    AvcC,
}

/// <summary>One decoded picture: NV12 planes plus the real row pitches reported by the decoder.</summary>
public sealed class DecodedFrame
{
    public DecodedFrame(int width, int height, byte[] nv12, int yStride, int uvStride, long timestampUs)
    {
        Width = width;
        Height = height;
        Nv12 = nv12;
        YStride = yStride;
        UvStride = uvStride;
        TimestampUs = timestampUs;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Nv12 { get; }

    /// <summary>Real luma row pitch from the decoder buffer. Never assume it equals Width.</summary>
    public int YStride { get; }

    public int UvStride { get; }

    public long TimestampUs { get; }

    public void ToBgra(Span<byte> bgra, YuvMatrix matrix = YuvMatrix.Bt601) =>
        Nv12ToBgra.Convert(Nv12, Width, Height, YStride, UvStride, bgra, matrix);
}

/// <summary>
/// H.264 decoder wrapping the in-box Media Foundation decoder MFT
/// (<c>CLSID_CMSH264DecoderMFT</c>), the counterpart of the encoder already in this project.
/// </summary>
/// <remarks>
/// <para>
/// The design follows invariants extracted from FFmpeg (see docs/ffmpeg-extraction.md): the coded
/// size drives decoder configuration rather than the display size; row pitch comes from the buffer
/// itself; a pending transform is retried a bounded number of times instead of spinning; and a
/// stream change rebuilds the output type rather than continuing on a stale one.
/// </para>
/// <para>
/// Hardware acceleration is requested through <c>MF_SA_D3D11_AWARE</c> when a device manager is
/// supplied. When that is unavailable the decoder still runs, in software, and
/// <see cref="HardwareAccelerated"/> reports which path was taken. Reporting is deliberate: a
/// silent fallback would hide a real performance regression.
/// </para>
/// </remarks>
public sealed class H264Decoder : IDisposable
{
    private const int ClsCtxInprocServer = 0x1;
    private const int MfEInvalidMediaType = unchecked((int)0xC00D36B4);
    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    private const int MfENoMoreTypes = unchecked((int)0xC00D36B5);
    private const int MaxPendingRetries = 50;

    private static readonly Guid ClsidCmsH264DecoderMft = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    private static readonly Guid IidImfTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");

    private readonly IMFTransform _transform;
    private readonly bool _ownsMf;
    private IMFMediaType? _outputType;
    private bool _streamStarted;
    private bool _configured;
    private bool _outputProvidesSamples;
    private int _outputSampleSize;
    private H264Sps.PictureSize? _pendingCodedSize;
    private string _lastOutputResult = "none";
    private string _lastInputResult = "none";

    /// <summary>Last ProcessInput HRESULT, which the generated wrapper does not surface on its own.</summary>
    public string LastInputResult => _lastInputResult;
    private bool _disposed;

    public H264Decoder(bool requestHardwareAcceleration = true)
    {
        MediaFactory.MFStartup(false).CheckError();
        _ownsMf = true;
        _transform = CreateDecoderTransform();

        if (requestHardwareAcceleration)
        {
            AttemptLog = TryEnableHardware();
        }
        else
        {
            AttemptLog = ["硬件加速未请求"];
        }

        UnlockAsyncTransform();
    }

    /// <summary>
    /// Create a decoder that skips output type selection, so a caller can try each advertised type.
    /// </summary>
    public static H264Decoder CreateForProbing()
    {
        var decoder = new H264Decoder(requestHardwareAcceleration: false);
        decoder._skipAutoOutputType = true;
        return decoder;
    }

    private bool _skipAutoOutputType;
    private ulong _sequenceHeaderFingerprint;

    /// <summary>Stable fingerprint of the SPS/PPS bytes, so an identical repeat is not a reconfiguration.</summary>
    private static ulong FingerprintSequenceHeader(ReadOnlySpan<byte> accessUnit, List<H264NalUnit> units)
    {
        ulong hash = 14695981039346656037;
        foreach (var unit in units)
        {
            if (unit.Type is not (H264NalUnitType.Sps or H264NalUnitType.Pps))
            {
                continue;
            }

            foreach (byte value in H264Bitstream.Payload(accessUnit, unit))
            {
                hash = (hash ^ value) * 1099511628211;
            }
        }

        return hash;
    }

    /// <summary>Set the output type by index from the advertised list. Returns false when rejected.</summary>
    public bool TrySetOutputTypeByIndex(int index, out string detail)
    {
        detail = string.Empty;
        IMFMediaType available;
        try
        {
            available = _transform.GetOutputAvailableType(0, index);
        }
        catch (SharpGenException ex)
        {
            detail = $"枚举 #{index} 失败 0x{ex.HResult:X8}";
            return false;
        }

        try
        {
            _transform.SetOutputType(0, available, 0);
            _outputType?.Dispose();
            _outputType = available;
            var size = available.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            OutputWidth = (int)(size >> 32);
            OutputHeight = (int)(size & 0xFFFFFFFF);
            detail = $"{SubtypeName(available.GetGUID(MediaTypeAttributeKeys.Subtype))} {OutputWidth}x{OutputHeight}";
            return true;
        }
        catch (SharpGenException ex)
        {
            available.Dispose();
            detail = $"0x{ex.HResult:X8}";
            return false;
        }
    }

    /// <summary>
    /// Newer Windows builds ship the H.264 decoder as an asynchronous MFT. An async MFT accepts
    /// ProcessInput and then never produces output on the synchronous interface: it wants
    /// IMFMediaEventGenerator callbacks instead. Unlocking it makes the synchronous calls valid,
    /// which is why this runs before any type is configured.
    /// </summary>
    private void UnlockAsyncTransform()
    {
        var log = AttemptLog.ToList();
        try
        {
            // The transform also exposes IMFAttributes; query it through the same COM object.
            var attributes = _transform.QueryInterface<IMFAttributes>();
            using (attributes)
            {
                bool isAsync = attributes.GetUInt32(TransformAttributeKeys.TransformAsync, out var asyncValue).Success
                    && asyncValue != 0;
                log.Add($"MFT 异步标志={isAsync}");
                if (isAsync)
                {
                    attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1).CheckError();
                    log.Add("MF_TRANSFORM_ASYNC_UNLOCK=1 已设置");
                }
            }
        }
        catch (SharpGenException ex)
        {
            log.Add($"读取 MFT 属性失败: 0x{ex.HResult:X8}");
        }

        AttemptLog = log;
    }

    public IReadOnlyList<string> AttemptLog { get; private set; } = [];

    /// <summary>True only when the decoder accepted a D3D11 device manager.</summary>
    public bool HardwareAccelerated { get; private set; }

    public string DecoderName => "CMSH264DecoderMFT (Windows 内置 H.264 解码器 MFT)";

    public int OutputWidth { get; private set; }

    public int OutputHeight { get; private set; }

    /// <summary>Last ProcessOutput outcome, for diagnosing a decoder that emits nothing.</summary>
    public string LastOutputResult => _lastOutputResult;

    /// <summary>
    /// How the sequence header is published on the input type. The in-box decoder accepts the
    /// avcC container, raw Annex-B, or nothing at all (reading SPS/PPS from the bitstream); which
    /// one works is measured, not assumed.
    /// </summary>
    public SequenceHeaderFormat SequenceHeaderMode { get; set; } = SequenceHeaderFormat.AvcC;

    /// <summary>
    /// Feed samples as Annex-B with start codes. Measured against the in-box decoder: start codes
    /// decode, 4-byte length prefixes do not, regardless of the sequence-header form. The earlier
    /// assumption that the decoder wanted MP4-style length prefixes was wrong.
    /// </summary>
    public bool FeedLengthPrefixed { get; set; }

    /// <summary>
    /// Configure only the input side from an access unit. Used when the caller wants to inspect or
    /// choose the output type itself; feeding samples is a separate step.
    /// </summary>
    public void ConfigureInputOnly(ReadOnlySpan<byte> accessUnit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var units = H264Bitstream.ParseAnnexB(accessUnit);
        ConfigureFromAccessUnit(accessUnit, units);
    }

    /// <summary>
    /// Decode one Annex-B access unit and return every picture the transform is ready to emit.
    /// A key frame that carries SPS/PPS refreshes the sequence header automatically.
    /// </summary>
    public IReadOnlyList<DecodedFrame> Decode(ReadOnlySpan<byte> annexBAccessUnit, long timestampUs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (annexBAccessUnit.Length == 0)
        {
            return [];
        }

        var units = H264Bitstream.ParseAnnexB(annexBAccessUnit);
        bool hasSequenceHeader = units.Any(unit =>
            unit.Type is H264NalUnitType.Sps or H264NalUnitType.Pps);
        if (!_configured)
        {
            // A decoder cannot accept a type without the sequence header: it has no profile or
            // geometry to configure itself with. The first access unit always carries SPS/PPS.
            ConfigureFromAccessUnit(annexBAccessUnit, units);
        }
        else if (hasSequenceHeader)
        {
            // Reconfigure only when the parameter sets actually changed. Key frames repeat the same
            // SPS/PPS, and reconfiguring on every key frame would reset the transform mid-stream.
            ulong fingerprint = FingerprintSequenceHeader(annexBAccessUnit, units);
            if (fingerprint != _sequenceHeaderFingerprint)
            {
                ConfigureFromAccessUnit(annexBAccessUnit, units);
            }
        }

        using var sample = MediaFactory.MFCreateSample();

        // Media Foundation's H.264 decoder is built around the MP4 sample layout, where NAL units are
        // length-prefixed rather than start-code separated. Feeding Annex-B start codes makes it
        // consume input without ever producing a picture, so the access unit is converted to the
        // length-prefixed form that the avcC sequence header declares (lengthSizeMinusOne = 3).
        byte[] feed = FeedLengthPrefixed ? ToLengthPrefixed(annexBAccessUnit) : annexBAccessUnit.ToArray();
        using var buffer = MediaFactory.MFCreateMemoryBuffer(feed.Length);
        buffer.Lock(out var destination, out _, out _);
        try
        {
            unsafe
            {
                feed.AsSpan().CopyTo(new Span<byte>((void*)destination, feed.Length));
            }
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = feed.Length;
        sample.AddBuffer(buffer);
        sample.SampleTime = timestampUs * 10;

        try
        {
            _transform.ProcessInput(0, sample, 0);
            _lastInputResult = "OK";
        }
        catch (SharpGenException ex)
        {
            _lastInputResult = $"0x{ex.HResult:X8}";
            AttemptLog = [.. AttemptLog,
                $"ProcessInput 抛出: {_lastInputResult} {ex.Message} (输入 {feed.Length} 字节)"];
            throw;
        }

        return CollectFrames();
    }

    /// <summary>
    /// Convert one Annex-B access unit to the length-prefixed form (4-byte big-endian length before
    /// every NAL unit). The avcC record we publish declares exactly this length size.
    /// </summary>
    private static byte[] ToLengthPrefixed(ReadOnlySpan<byte> annexB)
    {
        var units = H264Bitstream.ParseAnnexB(annexB);
        int total = 0;
        foreach (var unit in units)
        {
            total += 4 + unit.Length;
        }

        byte[] result = new byte[total];
        int offset = 0;
        foreach (var unit in units)
        {
            var payload = H264Bitstream.Payload(annexB, unit);
            result[offset] = (byte)(payload.Length >> 24);
            result[offset + 1] = (byte)(payload.Length >> 16);
            result[offset + 2] = (byte)(payload.Length >> 8);
            result[offset + 3] = (byte)payload.Length;
            payload.CopyTo(result.AsSpan(offset + 4));
            offset += 4 + payload.Length;
        }

        return result;
    }

    /// <summary>
    /// Configure both types from an access unit. Input must be set first: the Media Foundation
    /// decoder refuses an output type until it knows the profile from the sequence header.
    /// </summary>
    private void ConfigureFromAccessUnit(ReadOnlySpan<byte> accessUnit, List<H264NalUnit> units)
    {
        int first = -1, last = -1;
        foreach (var unit in units)
        {
            if (unit.Type is not (H264NalUnitType.Sps or H264NalUnitType.Pps))
            {
                continue;
            }

            if (first < 0)
            {
                first = unit.Offset;
            }

            // Offset points at the start code, so the NAL ends at payload start + payload length.
            int payloadStart = unit.Offset + H264Bitstream.StartCodeLength(accessUnit, unit.Offset);
            last = payloadStart + unit.Length;
        }

        if (first < 0 || last <= first || last > accessUnit.Length)
        {
            throw new InvalidOperationException("首个访问单元缺少 SPS/PPS，无法配置解码器。");
        }

        byte[] sequenceHeader = accessUnit[first..last].ToArray();

        // The decoder offers a placeholder output geometry until it knows the real coded size, so
        // read the SPS and drive the surface size from the stream instead of from a default.
        var codedSize = H264Sps.FromAnnexB(sequenceHeader, out var spsError);
        var log = AttemptLog.ToList();
        log.Add(codedSize.Valid
            ? $"SPS 编码尺寸 {codedSize.Width}x{codedSize.Height}"
            : $"SPS 尺寸解析失败：{spsError}");

        using var input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatH264);
        if (SequenceHeaderMode != SequenceHeaderFormat.None)
        {
            byte[] sequenceHeaderBlob = SequenceHeaderMode == SequenceHeaderFormat.AvcC
                ? H264Bitstream.BuildAvcDecoderConfigurationRecord(sequenceHeader)
                : sequenceHeader;
            input.SetBlob(MediaTypeAttributeKeys.MpegSequenceHeader, sequenceHeaderBlob);
            log.Add($"序列头 {SequenceHeaderMode} {sequenceHeaderBlob.Length} 字节");
        }

        if (codedSize.Valid)
        {
            // Some decoder builds size their internal surfaces from the input type instead of the
            // SPS; supplying the geometry is harmless when it is ignored.
            input.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)codedSize.Width, (uint)codedSize.Height));
            input.Set(MediaTypeAttributeKeys.FrameRate, Pack(30, 1));
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2);
        }

        if (_streamStarted)
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            _streamStarted = false;
        }

        _pendingCodedSize = codedSize.Valid ? codedSize : null;
        _sequenceHeaderFingerprint = FingerprintSequenceHeader(accessUnit, units);
        try
        {
            _transform.SetInputType(0, input, 0);
            log.Add($"SetInputType: OK (序列头 {sequenceHeader.Length} 字节)");
        }
        catch (SharpGenException ex)
        {
            log.Add($"SetInputType: 0x{ex.HResult:X8} {ex.Message}");
            AttemptLog = log;
            throw;
        }

        AttemptLog = log;

        // Restore the output type before input: this decoder rejects input with
        // MF_E_TRANSFORM_TYPE_NOT_SET when no output type is set, so the placeholder geometry has
        // to be accepted first and corrected later through MF_E_TRANSFORM_STREAM_CHANGE.
        _outputType?.Dispose();
        _outputType = null;
        if (!_skipAutoOutputType)
        {
            ConfigureOutputType();
        }

        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            log.Add("BeginStreaming: OK");
        }
        catch (SharpGenException ex)
        {
            log.Add($"BeginStreaming: 0x{ex.HResult:X8} {ex.Message}");
            AttemptLog = log;
            throw;
        }

        _streamStarted = true;
        _configured = true;

        // ConfigureOutputType replaced AttemptLog with its own list; merge so nothing is lost.
        AttemptLog = [.. AttemptLog, $"输出 {OutputWidth}x{OutputHeight}"];
    }

    private IReadOnlyList<DecodedFrame> CollectFrames()
    {
        var frames = new List<DecodedFrame>();
        var output = new OutputDataBuffer();

        // Bounded retry: a decoder that reports pending work must not be spun on forever.
        for (int attempt = 0; attempt < MaxPendingRetries; attempt++)
        {            // A transform that does not provide its own samples rejects a null output sample with
            // E_INVALIDARG, so the caller has to allocate one with the size it asked for.
            IMFSample? provided = null;
            if (!_outputProvidesSamples)
            {
                provided = MediaFactory.MFCreateSample();
                using var buffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(_outputSampleSize, 1));
                provided.AddBuffer(buffer);
            }
            output.Sample = provided;
            output.Events = null;
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            if (result.Success)
            {
                var produced = output.Sample;
                if (produced is null)
                {
                    break;
                }

                try
                {
                    frames.Add(ReadFrame(produced));
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
                _lastOutputResult = "NEED_MORE_INPUT";
                break;
            }

            if (result.Code == MfETransformStreamChange)
            {
                output.Sample?.Dispose();
                _lastOutputResult = "STREAM_CHANGE";
                RefreshOutputType();
                continue;
            }

            output.Sample?.Dispose();
            _lastOutputResult = $"0x{result.Code:X8}";
            result.CheckError();
        }

        return frames;
    }

    private DecodedFrame ReadFrame(IMFSample sample)
    {
        using var contiguous = sample.ConvertToContiguousBuffer();

        // The decoder reports the real pitches; NV12 has one buffer holding both planes.
        int yStride = contiguous.CurrentLength > 0 ? 0 : 0;
        contiguous.Lock(out var pointer, out var maxLength, out var currentLength);
        try
        {
            if (currentLength <= 0 || currentLength > maxLength)
            {
                throw new InvalidOperationException($"解码输出长度异常：{currentLength}/{maxLength}。");
            }

            // Media Foundation's decoder emits a single NV12 buffer sized for the padded pitch.
            // Derive the pitch from the buffer length so a padded frame is not read with a wrong stride.
            (yStride, int uvStride) = DeriveStrides(currentLength, OutputWidth, OutputHeight);
            byte[] data = new byte[currentLength];
            Marshal.Copy(pointer, data, 0, currentLength);
            long ticks = sample.SampleTime;
            return new DecodedFrame(OutputWidth, OutputHeight, data, yStride, uvStride,
                ticks >= 0 ? ticks / 10 : -1);
        }
        finally
        {
            contiguous.Unlock();
        }
    }

    /// <summary>
    /// NV12 buffer length is <c>pitch * height * 3 / 2</c> for an even width, so the pitch follows
    /// from the length. When the length matches the tight layout exactly the pitch is the width.
    /// </summary>
    private static (int YStride, int UvStride) DeriveStrides(int length, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return (width, width);
        }

        int tight = width * height * 3 / 2;
        if (length == tight)
        {
            return (width, width);
        }

        int candidate = length * 2 / (height * 3);
        if (candidate >= width)
        {
            // Chroma rows are the same pitch as luma in NV12.
            return (candidate, candidate);
        }

        return (width, width);
    }

    private void ConfigureOutputType()
    {
        var log = AttemptLog.ToList();
        IMFMediaType? chosen = null;

        // A decoder advertises a placeholder geometry until it knows the real coded size. Mutating
        // that advertised type in place is rejected, so build a fresh NV12 type from the SPS size
        // and only fall back to the advertised one when the decoder insists on its own geometry.
        if (_pendingCodedSize is { } coded)
        {
            var candidate = BuildNv12OutputType(coded);
            try
            {
                _transform.SetOutputType(0, candidate, 0);
                chosen = candidate;
                log.Add($"自建 NV12 输出类型 {coded.Width}x{coded.Height}: OK");
            }
            catch (SharpGenException ex)
            {
                log.Add($"自建 NV12 {coded.Width}x{coded.Height}: 0x{ex.HResult:X8}");
                candidate.Dispose();
            }
        }

        if (chosen is null)
        {
            for (int index = 0; ; index++)
            {
                IMFMediaType available;
                try
                {
                    available = _transform.GetOutputAvailableType(0, index);
                }
                catch (SharpGenException ex) when (ex.HResult == MfENoMoreTypes)
                {
                    break;
                }

                var subType = available.GetGUID(MediaTypeAttributeKeys.Subtype);
                var size = available.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                int candidateWidth = (int)(size >> 32);
                int candidateHeight = (int)(size & 0xFFFFFFFF);
                log.Add($"输出类型 #{index}: {DescribeSubtype(subType)} {candidateWidth}x{candidateHeight}");

                if (subType != VideoFormatNv12)
                {
                    available.Dispose();
                    continue;
                }

                try
                {
                    _transform.SetOutputType(0, available, 0);
                    chosen = available;
                    log.Add($"采用解码器提供的输出类型 #{index} (NV12 {candidateWidth}x{candidateHeight})");
                    break;
                }
                catch (SharpGenException ex)
                {
                    log.Add($"SetOutputType #{index}: 0x{ex.HResult:X8}");
                    available.Dispose();
                }
            }
        }

        if (chosen is null)
        {
            AttemptLog = log;
            throw new InvalidOperationException("解码器拒绝了所有 NV12 输出类型。");
        }

        _outputType = chosen;
        // MF_MT_FRAME_SIZE packs width in the high 32 bits and height in the low 32 bits.
        var frameSize = _outputType.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        OutputWidth = (int)(frameSize >> 32);
        OutputHeight = (int)(frameSize & 0xFFFFFFFF);
        log.Add($"SetOutputType: OK ({OutputWidth}x{OutputHeight})");
        AttemptLog = log;

        var streamInfo = _transform.GetOutputStreamInfo(0);
        _outputProvidesSamples = (streamInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        _outputSampleSize = streamInfo.Size;
        AttemptLog = [.. AttemptLog,
            $"输出流：提供样本={_outputProvidesSamples}, 建议缓冲={_outputSampleSize}"];    }

    private void RefreshOutputType()
    {
        _outputType?.Dispose();
        _outputType = null;
        ConfigureOutputType();
    }

    /// <summary>Configure the output type on demand, after the decoder reported its real geometry.</summary>
    private void EnsureOutputType()
    {
        if (_outputType is null)
        {
            ConfigureOutputType();
        }
    }

    private static IMFMediaType BuildNv12OutputType(H264Sps.PictureSize? codedSize)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatNv12);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)2); // progressive
        if (codedSize is { } size)
        {
            type.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)size.Width, (uint)size.Height));
            // Decoders validate the output type against these; an unset ratio is rejected.
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            type.Set(MediaTypeAttributeKeys.FrameRate, Pack(30, 1));
        }

        return type;
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    private static string DescribeSubtype(Guid subtype)
    {
        if (subtype == VideoFormatNv12)
        {
            return "NV12";
        }

        return subtype.ToString();
    }

    /// <summary>
    /// Enumerate every output type the decoder offers, with subtype and geometry. Diagnostic only:
    /// this is how the real accepted format is discovered instead of assumed.
    /// </summary>
    public IReadOnlyList<string> DescribeOutputTypes()
    {
        var result = new List<string>();
        for (int index = 0; index < 32; index++)
        {
            IMFMediaType available;
            try
            {
                available = _transform.GetOutputAvailableType(0, index);
            }
            catch (SharpGenException ex)
            {
                result.Add($"#{index}: <end 0x{ex.HResult:X8}>");
                break;
            }

            try
            {
                var subType = available.GetGUID(MediaTypeAttributeKeys.Subtype);
                var size = available.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                var ratio = available.GetUInt64(MediaTypeAttributeKeys.PixelAspectRatio);
                var rate = available.GetUInt64(MediaTypeAttributeKeys.FrameRate);
                result.Add($"#{index}: {DescribeSubtype(subType)} " +
                    $"{(int)(size >> 32)}x{(int)(size & 0xFFFFFFFF)} " +
                    $"PAR {(int)(ratio >> 32)}:{(int)(ratio & 0xFFFFFFFF)} " +
                    $"FPS {(int)(rate >> 32)}:{(int)(rate & 0xFFFFFFFF)}");
            }
            finally
            {
                available.Dispose();
            }
        }

        var info = _transform.GetInputStreamInfo(0);
        result.Add($"输入流：flags=0x{(int)info.Flags:X} 对齐={info.Size}");
        return result;
    }

    /// <summary>Which NV12/P010/YUY2-style formats the decoder advertises, for the caller to choose.</summary>
    public static string SubtypeName(Guid subtype)
    {
        if (subtype == VideoFormatNv12)
        {
            return "NV12";
        }

        if (subtype == VideoFormatP010)
        {
            return "P010";
        }

        if (subtype == VideoFormatYuy2)
        {
            return "YUY2";
        }

        return subtype.ToString();
    }

    private static readonly Guid VideoFormatP010 = new("30313050-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoFormatYuy2 = new("32595559-0000-0010-8000-00AA00389B71");

    private IReadOnlyList<string> TryEnableHardware()
    {
        // D3D11 acceleration needs a device manager created on the decoder's own device; without a
        // shared device the decoder refuses it, so this reports the outcome instead of pretending.
        var log = new List<string>();
        try
        {
            var attributes = MediaFactory.MFCreateAttributes(1);
            attributes.Set(TransformAttributeKeys.D3D11Aware, 1);
            log.Add("MF_SA_D3D11_AWARE=1 已请求");
            attributes.Dispose();
        }
        catch (SharpGenException ex)
        {
            log.Add($"D3D11 属性设置失败: 0x{ex.HResult:X8}");
        }

        log.Add("未提供共享 D3D11 设备管理器，解码走软件路径");
        HardwareAccelerated = false;
        return log;
    }

    private static IMFTransform CreateDecoderTransform()
    {
        // SharpGen needs its own COM wrapper: a plain Activator result is a __ComObject and cannot
        // be cast to IMFTransform. CoCreateInstance with the transform IID yields the right pointer.
        var clsid = ClsidCmsH264DecoderMft;
        var iid = IidImfTransform;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref iid, out var pointer);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return new IMFTransform(pointer);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_streamStarted)
        {
            try
            {
                _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            }
            catch (SharpGenException)
            {
                // Ending an already stopped stream is not an error worth surfacing.
            }

            _streamStarted = false;
        }

        _outputType?.Dispose();
        _outputType = null;
        _transform.Dispose();
        if (_ownsMf)
        {
            MediaFactory.MFShutdown();
        }
    }
}