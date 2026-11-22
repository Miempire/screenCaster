namespace ScreenCast.Windows.Media;

/// <summary>
/// Minimal H.264 sequence parameter set reader. Only the fields needed to learn the coded picture
/// size are parsed, but the whole header up to the size fields is walked correctly, because those
/// fields are variable-length and cannot be skipped without decoding every preceding one.
/// </summary>
/// <remarks>
/// The size that matters for decoder configuration is the coded size, not the display size: a
/// stream can carry cropping that differs from the macroblock grid. Media Foundation and D3D11 both
/// configure surfaces from the coded geometry, so that is what this returns.
/// </remarks>
public static class H264Sps
{
    public readonly record struct PictureSize(int Width, int Height, bool Valid);

    /// <summary>Parse the coded picture size from an Annex-B stream containing an SPS NAL.</summary>
    public static PictureSize FromAnnexB(ReadOnlySpan<byte> annexB) => FromAnnexB(annexB, out _);

    public static PictureSize FromAnnexB(ReadOnlySpan<byte> annexB, out string? error)
    {
        error = null;
        bool sawSps = false;
        foreach (var nal in H264Bitstream.ParseAnnexB(annexB))
        {
            if (nal.Type is not (H264NalUnitType.Sps or H264NalUnitType.SubsetSps))
            {
                continue;
            }

            sawSps = true;
            var payload = H264Bitstream.Payload(annexB, nal);
            // Skip the NAL header byte, then remove emulation prevention bytes.
            if (payload.Length < 2)
            {
                continue;
            }

            byte[] rbsp = RemoveEmulationPrevention(payload[1..]);
            var size = ParseRbsp(rbsp, out var reason);
            if (size.Valid)
            {
                return size;
            }

            error = reason ?? "SPS 解析未返回尺寸";
        }

        error ??= sawSps ? "SPS 解析失败" : "未找到 SPS NAL";
        return default;
    }

    /// <summary>Parse from an already unescaped RBSP (the SPS payload without the NAL header).</summary>
    public static PictureSize ParseRbsp(ReadOnlySpan<byte> rbsp) => ParseRbsp(rbsp, out _);

    /// <summary>Parse with a human-readable failure reason, so a broken stream is diagnosable.</summary>
    public static PictureSize ParseRbsp(ReadOnlySpan<byte> rbsp, out string? error)
    {
        error = null;
        var reader = new BitReader(rbsp);
        try
        {
            int profileIdc = reader.ReadBits(8);
            reader.ReadBits(8); // constraint flags and reserved bits
            reader.ReadBits(8); // level_idc
            reader.ReadUe();    // seq_parameter_set_id

            int chromaFormatIdc = 1;
            if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chromaFormatIdc = reader.ReadUe();
                if (chromaFormatIdc == 3)
                {
                    reader.ReadBits(1); // separate_colour_plane_flag
                }

                reader.ReadUe(); // bit_depth_luma_minus8
                reader.ReadUe(); // bit_depth_chroma_minus8
                reader.ReadBits(1); // qpprime_y_zero_transform_bypass_flag
                if (reader.ReadBits(1) != 0) // seq_scaling_matrix_present_flag
                {
                    int count = chromaFormatIdc == 3 ? 12 : 8;
                    for (int i = 0; i < count; i++)
                    {
                        if (reader.ReadBits(1) != 0)
                        {
                            SkipScalingList(reader, i < 6 ? 16 : 64);
                        }
                    }
                }
            }

            reader.ReadUe(); // log2_max_frame_num_minus4
            int picOrderCntType = reader.ReadUe();
            if (picOrderCntType == 0)
            {
                reader.ReadUe(); // log2_max_pic_order_cnt_lsb_minus4
            }
            else if (picOrderCntType == 1)
            {
                reader.ReadBits(1); // delta_pic_order_always_zero_flag
                reader.ReadSe();    // offset_for_non_ref_pic
                reader.ReadSe();    // offset_for_top_to_bottom_field
                int cycle = reader.ReadUe();
                for (int i = 0; i < cycle; i++)
                {
                    reader.ReadSe();
                }
            }

            reader.ReadUe();    // max_num_ref_frames
            reader.ReadBits(1); // gaps_in_frame_num_value_allowed_flag

            int widthInMbs = reader.ReadUe() + 1;
            int heightInMapUnits = reader.ReadUe() + 1;
            int frameMbsOnly = reader.ReadBits(1);
            if (frameMbsOnly == 0)
            {
                reader.ReadBits(1); // mb_adaptive_frame_field_flag
            }

            reader.ReadBits(1); // direct_8x8_inference_flag

            int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
            if (reader.ReadBits(1) != 0) // frame_cropping_flag
            {
                cropLeft = reader.ReadUe();
                cropRight = reader.ReadUe();
                cropTop = reader.ReadUe();
                cropBottom = reader.ReadUe();
            }

            int subWidthC = chromaFormatIdc is 1 or 2 ? 2 : 1;
            int subHeightC = chromaFormatIdc == 1 ? 2 : 1;
            int cropUnitX = chromaFormatIdc == 0 ? 1 : subWidthC;
            int cropUnitY = (chromaFormatIdc == 0 ? 1 : subHeightC) * (2 - frameMbsOnly);

            int width = (widthInMbs * 16) - ((cropLeft + cropRight) * cropUnitX);
            int height = ((2 - frameMbsOnly) * heightInMapUnits * 16) - ((cropTop + cropBottom) * cropUnitY);
            if (width <= 0 || height <= 0)
            {
                error = $"计算尺寸无效 {width}x{height}";
                return default;
            }

            return new PictureSize(width, height, true);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or InvalidOperationException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return default;
        }
    }

    private static void SkipScalingList(BitReader reader, int size)
    {
        int lastScale = 8;
        int nextScale = 8;
        for (int i = 0; i < size; i++)
        {
            if (nextScale != 0)
            {
                int delta = reader.ReadSe();
                nextScale = ((lastScale + delta) + 256) % 256;
            }

            lastScale = nextScale == 0 ? lastScale : nextScale;
        }
    }

    /// <summary>Drop 0x000003 escape bytes: the 03 exists only to stop a start code from appearing.</summary>
    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> escaped)
    {
        byte[] result = new byte[escaped.Length];
        int written = 0;
        int zeros = 0;
        foreach (byte value in escaped)
        {
            if (zeros >= 2 && value == 0x03)
            {
                zeros = 0;
                continue;
            }

            result[written++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }

        return result[..written];
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bit;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bit = 0;
        }

        public int ReadBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int byteIndex = _bit >> 3;
                if (byteIndex >= _data.Length)
                {
                    throw new IndexOutOfRangeException("SPS 位流读取越界。");
                }

                int shift = 7 - (_bit & 7);
                value = (value << 1) | ((_data[byteIndex] >> shift) & 1);
                _bit++;
            }

            return value;
        }

        /// <summary>Unsigned exp-Golomb: count leading zeros, then read that many bits after the one.</summary>
        public int ReadUe()
        {
            int leadingZeros = 0;
            while (true)
            {
                if (leadingZeros > 31)
                {
                    throw new InvalidOperationException("exp-Golomb 前导零过多。");
                }

                if (ReadBits(1) != 0)
                {
                    break;
                }

                leadingZeros++;
            }

            if (leadingZeros == 0)
            {
                return 0;
            }

            return ((1 << leadingZeros) - 1) + ReadBits(leadingZeros);
        }

        /// <summary>Signed exp-Golomb mapping used by the spec.</summary>
        public int ReadSe()
        {
            int value = ReadUe();
            return (value & 1) != 0 ? (value + 1) / 2 : -(value / 2);
        }
    }
}