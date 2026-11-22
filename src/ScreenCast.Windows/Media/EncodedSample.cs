namespace ScreenCast.Windows.Media;

/// <summary>One H.264 access unit produced by the encoder, already normalized to Annex-B.</summary>
public sealed record EncodedSample(
    byte[] Data,
    long TimestampUs,
    bool IsKeyFrame,
    bool ConvertedFromAvcc,
    IReadOnlyList<H264NalUnit> NalUnits)
{
    public int ByteLength => Data.Length;

    public H264NalSummary Summary => H264Bitstream.Summarize(Data);
}
