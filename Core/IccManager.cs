namespace DeltaColorManager.Core;

/// <summary>ICC 配置文件的列举与 vcgt（视频卡伽马表）解析。</summary>
internal static class IccManager
{
    public static string ColorDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");

    /// <summary>列出系统颜色目录里的全部 .icc / .icm 文件名。</summary>
    public static List<string> ListProfiles()
    {
        if (!Directory.Exists(ColorDir)) return new List<string>();
        return Directory.EnumerateFiles(ColorDir)
            .Select(Path.GetFileName)
            .Where(n => n != null &&
                        (n.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) ||
                         n.EndsWith(".icm", StringComparison.OrdinalIgnoreCase)))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 解析 ICC 的 vcgt 标签（表格式 type 0 / 公式型 type 1），插值成 768 项（R/G/B 各 256）LUT。
    /// 无 vcgt 标签（如纯矩阵 profile）返回 null → 调用方回退线性表。
    /// </summary>
    public static ushort[]? ParseVcgt(string fileName)
    {
        try
        {
            string full = Path.Combine(ColorDir, fileName);
            if (!File.Exists(full)) return null;

            byte[] d = File.ReadAllBytes(full);
            if (d.Length < 132) return null;

            int tagCount = (int)Be32(d, 128);
            if (tagCount < 0 || tagCount > 1024) return null;

            for (int t = 0; t < tagCount; t++)
            {
                int off = 132 + t * 12;
                if (off + 12 > d.Length) break;
                if (d[off] == (byte)'v' && d[off + 1] == (byte)'c' &&
                    d[off + 2] == (byte)'g' && d[off + 3] == (byte)'t')
                {
                    int dataOff = (int)Be32(d, off + 4);
                    int dataSize = (int)Be32(d, off + 8);
                    return ParseVcgtData(d, dataOff, dataSize);
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static ushort[]? ParseVcgtData(byte[] d, int off, int size)
    {
        // Apple vcgt tag layout:
        //   off+0..3 : 'vcgt' signature
        //   off+4..7 : reserved
        //   off+8..11: type (0=table, 1=formula)
        if (off < 0 || off + 18 > d.Length || size < 18) return null;

        uint type = Be32(d, off + 8);
        var lut = new ushort[768];

        if (type == 0)
        {
            // 表格式 vcgt
            int channels = (d[off + 12] << 8) | d[off + 13];
            int entries  = (d[off + 14] << 8) | d[off + 15];
            int entrySize = (d[off + 16] << 8) | d[off + 17];
            if (channels != 3 || entrySize != 2 || entries < 2) return null;

            int tableOff = off + 18;
            if (tableOff + channels * entries * 2 > d.Length) return null;

            for (int i = 0; i < 256; i++)
            {
                double pos = i * (entries - 1) / 255.0;
                for (int c = 0; c < 3; c++)
                {
                    int baseIdx = tableOff + c * entries * 2;
                    lut[c * 256 + i] = SampleChannel(d, baseIdx, entries, pos);
                }
            }
            return lut;
        }

        if (type == 1)
        {
            // 公式型 vcgt：每通道 gamma(s15.16 4B) / min(u16 2B) / max(u16 2B)
            if (off + 12 + 24 > d.Length) return null;
            for (int c = 0; c < 3; c++)
            {
                int b = off + 12 + c * 8;
                int rawGamma = (d[b] << 24) | (d[b + 1] << 16) | (d[b + 2] << 8) | d[b + 3];
                double gamma = rawGamma / 65536.0;
                double min = ((d[b + 4] << 8) | d[b + 5]) / 65535.0;
                double max = ((d[b + 6] << 8) | d[b + 7]) / 65535.0;
                for (int i = 0; i < 256; i++)
                {
                    double x = i / 255.0;
                    double y = min + (max - min) * Math.Pow(x, gamma);
                    lut[c * 256 + i] = (ushort)Math.Round(Math.Clamp(y, 0.0, 1.0) * 65535.0);
                }
            }
            return lut;
        }

        return null; // 未知类型
    }

    private static ushort SampleChannel(byte[] d, int baseIdx, int entries, double pos)
    {
        int i0 = (int)Math.Floor(pos);
        int i1 = Math.Min(i0 + 1, entries - 1);
        double frac = pos - i0;
        ushort v0 = (ushort)((d[baseIdx + i0 * 2] << 8) | d[baseIdx + i0 * 2 + 1]);
        ushort v1 = (ushort)((d[baseIdx + i1 * 2] << 8) | d[baseIdx + i1 * 2 + 1]);
        return (ushort)Math.Round(v0 + (v1 - v0) * frac);
    }

    private static uint Be32(byte[] d, int o) =>
        (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
}
