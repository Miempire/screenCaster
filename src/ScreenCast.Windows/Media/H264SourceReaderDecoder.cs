using System.IO;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace ScreenCast.Windows.Media;

/// <summary>
/// H.264 decoding through the Media Foundation Source Reader.
/// </summary>
/// <remarks>
/// <para>
/// The Source Reader owns the H.264 parser, access-unit splitting, decoder creation and the output
/// type negotiation. That removes the two failure modes that a hand-wired MFT hits: the decoder
/// advertises a placeholder output geometry until it has seen a sequence header, and it rejects a
/// caller-built output type that disagrees with its own list. This path lets the platform decide and
/// then reports what it actually produced.
/// </para>
/// <para>
/// The reader is created from a URL, so this is the file/replay decoder. Live frames need a byte
/// stream fed from the network instead; that is a separate integration and is not claimed here.
/// </para>
/// </remarks>
public sealed class H264SourceReaderDecoder : IDisposable
{
    private static readonly Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");

    private readonly IMFSourceReader _reader;
    private readonly bool _ownsMf;
    private bool _disposed;

    public H264SourceReaderDecoder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Decode input does not exist.", path);
        }

        MediaFactory.MFStartup(false).CheckError();
        _ownsMf = true;

        using var attributes = MediaFactory.MFCreateAttributes(1);
        // Ask for the decoder to be enabled and for NV12 output so no colour converter is inserted.
        attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1);

        _reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);

        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatNv12);
        _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, outputType);

        // Ask for the native size the reader settled on, rather than assuming anything.
        using var current = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        var size = current.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        Width = (int)(size >> 32);
        Height = (int)(size & 0xFFFFFFFF);
        var subType = current.GetGUID(MediaTypeAttributeKeys.Subtype);
        OutputSubtype = subType == VideoFormatNv12 ? "NV12" : subType.ToString();
    }

    public int Width { get; }

    public int Height { get; }

    public string OutputSubtype { get; }

    /// <summary>Read the next decoded picture, or null at end of stream.</summary>
    public DecodedFrame? ReadNext(long fallbackTimestampUs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A reader can return a stream tick or a format change before the first sample; both are
        // normal and must be skipped rather than treated as end of stream.
        for (int guard = 0; guard < 16; guard++)
        {
            var sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream,
                SourceReaderControlFlag.None, out _, out var streamFlags, out _);

            if ((streamFlags & SourceReaderFlag.EndOfStream) != 0)
            {
                sample?.Dispose();
                return null;
            }

            if (sample is null)
            {
                continue;
            }

            try
            {
                using var contiguous = sample.ConvertToContiguousBuffer();
                contiguous.Lock(out var pointer, out var maxLength, out var currentLength);
                try
                {
                    if (currentLength <= 0 || currentLength > maxLength)
                    {
                        continue;
                    }

                    byte[] data = new byte[currentLength];
                    Marshal.Copy(pointer, data, 0, currentLength);
                    long ticks = sample.SampleTime;
                    int width = Width > 0 ? Width : 0;
                    int height = Height > 0 ? Height : 0;
                    (int yStride, int uvStride) = DeriveStrides(currentLength, width, height);
                    return new DecodedFrame(width, height, data, yStride, uvStride,
                        ticks >= 0 ? ticks / 10 : fallbackTimestampUs);
                }
                finally
                {
                    contiguous.Unlock();
                }
            }
            finally
            {
                sample.Dispose();
            }
        }

        return null;
    }

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
        return candidate >= width ? (candidate, candidate) : (width, width);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reader.Dispose();
        if (_ownsMf)
        {
            MediaFactory.MFShutdown();
        }
    }
}

