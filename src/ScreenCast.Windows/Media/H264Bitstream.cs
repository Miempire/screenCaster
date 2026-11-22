using System.Buffers.Binary;
using System.IO;

namespace ScreenCast.Windows.Media;

public enum H264NalUnitType
{
    NonIdrSlice = 1,
    DataPartitionA = 2,
    DataPartitionB = 3,
    DataPartitionC = 4,
    IdrSlice = 5,
    Sei = 6,
    Sps = 7,
    Pps = 8,
    AccessUnitDelimiter = 9,
    EndOfSequence = 10,
    EndOfStream = 11,
    FillerData = 12,
    SpsExtension = 13,
    PrefixNalUnit = 14,
    SubsetSps = 15,
}

public readonly record struct H264NalUnit(int Offset, int Length, H264NalUnitType Type, int NalRefIdc);

/// <summary>Aggregate NAL statistics used by the smoke verification.</summary>
public readonly record struct H264NalSummary(int Total, int Sps, int Pps, int Idr, int NonIdr, int Sei, int Other, long Bytes);

/// <summary>Annex-B parsing, AVCC-to-Annex-B conversion and NAL inspection.</summary>
public static class H264Bitstream
{
    public static bool HasAnnexBStartCode(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1)
            return true;
        return data.Length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1;
    }

    /// <summary>Length of the Annex-B start code at <paramref name="index"/>, or 0 when none starts there.</summary>
    public static int StartCodeLength(ReadOnlySpan<byte> data, int index)
    {
        if (index + 3 < data.Length && data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 0 && data[index + 3] == 1)
            return 4;
        if (index + 2 < data.Length && data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1)
            return 3;
        return 0;
    }

    public static List<H264NalUnit> ParseAnnexB(ReadOnlySpan<byte> data)
    {
        var units = new List<H264NalUnit>();
        var index = 0;
        while (index < data.Length)
        {
            var startCode = StartCodeLength(data, index);
            if (startCode == 0)
            {
                index++;
                continue;
            }

            var nalStart = index + startCode;
            if (nalStart >= data.Length)
                break;

            var next = nalStart;
            while (next < data.Length && StartCodeLength(data, next) == 0)
                next++;

            var length = next - nalStart;
            var header = data[nalStart];
            units.Add(new H264NalUnit(index, length, (H264NalUnitType)(header & 0x1F), (header >> 5) & 0x03));
            index = next;
        }

        return units;
    }

    /// <summary>
    /// The NAL bytes without the leading start code. <see cref="H264NalUnit.Offset"/> points at the
    /// start code, not at the NAL header, so slicing from Offset would include 00 00 01 and shift
    /// every parsed field.
    /// </summary>
    public static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> data, H264NalUnit unit)
    {
        int startCode = StartCodeLength(data, unit.Offset);
        int start = unit.Offset + startCode;
        if (start >= data.Length)
        {
            return default;
        }

        int length = Math.Min(unit.Length, data.Length - start);
        return data.Slice(start, length);
    }

    /// <summary>
    /// Build an AVCDecoderConfigurationRecord (the MP4 "avcC" payload) from an Annex-B stream that
    /// contains SPS and PPS. Media Foundation's H.264 decoder expects this container format in
    /// MF_MT_MPEG_SEQUENCE_HEADER; handing it raw Annex-B leaves the decoder without a profile, so
    /// it keeps its placeholder output geometry and never emits a picture.
    /// </summary>
    public static byte[] BuildAvcDecoderConfigurationRecord(ReadOnlySpan<byte> annexB)
    {
        byte[]? sps = null;
        byte[]? pps = null;
        foreach (var nal in ParseAnnexB(annexB))
        {
            var payload = Payload(annexB, nal);
            if (payload.Length < 2)
            {
                continue;
            }

            if (nal.Type == H264NalUnitType.Sps && sps is null)
            {
                sps = payload.ToArray();
            }
            else if (nal.Type == H264NalUnitType.Pps && pps is null)
            {
                pps = payload.ToArray();
            }
        }

        if (sps is null || pps is null || sps.Length < 4)
        {
            throw new FormatException("构建 avcC 需要 SPS 与 PPS。");
        }

        using var stream = new MemoryStream(11 + sps.Length + pps.Length);
        stream.WriteByte(1);              // configurationVersion
        stream.WriteByte(sps[1]);         // AVCProfileIndication
        stream.WriteByte(sps[2]);         // profile_compatibility
        stream.WriteByte(sps[3]);         // AVCLevelIndication
        stream.WriteByte(0xFF);           // 6 reserved bits + lengthSizeMinusOne = 3
        stream.WriteByte(0xE1);           // 3 reserved bits + numOfSequenceParameterSets = 1
        stream.WriteByte((byte)(sps.Length >> 8));
        stream.WriteByte((byte)sps.Length);
        stream.Write(sps);
        stream.WriteByte(1);              // numOfPictureParameterSets
        stream.WriteByte((byte)(pps.Length >> 8));
        stream.WriteByte((byte)pps.Length);
        stream.Write(pps);
        return stream.ToArray();
    }

    /// <summary>
    /// Split an Annex-B stream into access units. A unit is cut at an access-unit delimiter only
    /// once it already contains a slice: a stream can carry a delimiter immediately after another,
    /// and cutting there would produce a unit with only parameter sets and no picture.
    /// </summary>
    public static List<byte[]> SplitAccessUnits(ReadOnlySpan<byte> stream)
    {
        var units = new List<byte[]>();
        var nalUnits = ParseAnnexB(stream);
        if (nalUnits.Count == 0)
        {
            return units;
        }

        int currentStart = -1;
        bool hasSlice = false;
        foreach (var nal in nalUnits)
        {
            bool isDelimiter = nal.Type == H264NalUnitType.AccessUnitDelimiter;
            bool isSlice = nal.Type is H264NalUnitType.IdrSlice or H264NalUnitType.NonIdrSlice;

            if (currentStart < 0)
            {
                currentStart = nal.Offset;
            }
            else if ((isDelimiter && hasSlice) || (isSlice && hasSlice))
            {
                if (nal.Offset > currentStart)
                {
                    units.Add(stream[currentStart..nal.Offset].ToArray());
                }

                currentStart = nal.Offset;
                hasSlice = false;
            }

            if (isSlice)
            {
                hasSlice = true;
            }
        }

        if (currentStart >= 0 && stream.Length > currentStart)
        {
            units.Add(stream[currentStart..].ToArray());
        }

        return units;
    }

    /// <summary>True when the sample starts with a big-endian NAL length instead of a start code.</summary>
    public static bool LooksLikeAvcc(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5 || HasAnnexBStartCode(data))
            return false;
        var declared = BinaryPrimitives.ReadInt32BigEndian(data);
        return declared > 0 && declared <= data.Length - 4;
    }

    /// <summary>
    /// Normalizes one encoded sample to Annex-B. Annex-B samples pass through unchanged; AVCC samples
    /// are rewritten with 4-byte start codes and get the parameter sets from
    /// MF_MT_MPEG_SEQUENCE_HEADER prepended, so the first written access unit is self-contained.
    /// </summary>
    public static byte[] NormalizeToAnnexB(ReadOnlySpan<byte> sample, ReadOnlySpan<byte> avcConfigurationRecord, out bool converted)
    {
        converted = false;
        if (HasAnnexBStartCode(sample))
            return sample.ToArray();

        if (avcConfigurationRecord.Length < 7)
            throw new InvalidDataException(
                $"编码器输出既不是 Annex-B，也没有可用的 AVCDecoderConfigurationRecord（{avcConfigurationRecord.Length} 字节）。");

        var lengthSize = (avcConfigurationRecord[4] & 0x03) + 1;
        var parameterSets = BuildParameterSets(avcConfigurationRecord);
        using var output = new MemoryStream(parameterSets.Length + sample.Length + 16);
        output.Write(parameterSets);

        var read = 0;
        while (read < sample.Length)
        {
            if (read + lengthSize > sample.Length)
                throw new InvalidDataException("AVCC 样本的 NAL 长度前缀越界。");
            var declared = lengthSize switch
            {
                4 => BinaryPrimitives.ReadInt32BigEndian(sample[read..]),
                2 => BinaryPrimitives.ReadUInt16BigEndian(sample[read..]),
                _ => sample[read],
            };
            read += lengthSize;
            if (declared <= 0 || read + declared > sample.Length)
                throw new InvalidDataException($"AVCC NAL 长度 {declared} 与样本剩余 {sample.Length - read} 字节不符。");

            output.Write([0, 0, 0, 1]);
            output.Write(sample.Slice(read, declared));
            read += declared;
        }

        converted = true;
        return output.ToArray();
    }

    private static byte[] BuildParameterSets(ReadOnlySpan<byte> record)
    {
        var result = new List<byte>(64);
        var cursor = 6;
        var spsCount = record[5] & 0x1F;
        for (var i = 0; i < spsCount; i++)
        {
            if (cursor + 2 > record.Length)
                throw new InvalidDataException("AVC 配置记录的 SPS 长度越界。");
            var length = BinaryPrimitives.ReadUInt16BigEndian(record[cursor..]);
            cursor += 2;
            if (length == 0 || cursor + length > record.Length)
                throw new InvalidDataException("AVC 配置记录的 SPS 数据越界。");
            result.AddRange([0, 0, 0, 1]);
            result.AddRange(record.Slice(cursor, length).ToArray());
            cursor += length;
        }

        if (cursor >= record.Length)
            return result.ToArray();
        var ppsCount = record[cursor++];
        for (var i = 0; i < ppsCount; i++)
        {
            if (cursor + 2 > record.Length)
                throw new InvalidDataException("AVC 配置记录的 PPS 长度越界。");
            var length = BinaryPrimitives.ReadUInt16BigEndian(record[cursor..]);
            cursor += 2;
            if (length == 0 || cursor + length > record.Length)
                throw new InvalidDataException("AVC 配置记录的 PPS 数据越界。");
            result.AddRange([0, 0, 0, 1]);
            result.AddRange(record.Slice(cursor, length).ToArray());
            cursor += length;
        }

        return result.ToArray();
    }

    public static H264NalSummary Summarize(ReadOnlySpan<byte> annexB)
    {
        var units = ParseAnnexB(annexB);
        int sps = 0, pps = 0, idr = 0, nonIdr = 0, sei = 0, other = 0;
        foreach (var unit in units)
        {
            switch (unit.Type)
            {
                case H264NalUnitType.Sps:
                case H264NalUnitType.SubsetSps:
                    sps++;
                    break;
                case H264NalUnitType.Pps:
                    pps++;
                    break;
                case H264NalUnitType.IdrSlice:
                    idr++;
                    break;
                case H264NalUnitType.NonIdrSlice:
                    nonIdr++;
                    break;
                case H264NalUnitType.Sei:
                    sei++;
                    break;
                default:
                    other++;
                    break;
            }
        }

        return new H264NalSummary(units.Count, sps, pps, idr, nonIdr, sei, other, annexB.Length);
    }
}
