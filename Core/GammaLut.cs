namespace DeltaColorManager.Core;

/// <summary>
/// 伽马表合成与应用。
/// 管线：vcgt 基线（来自 ICC，可为线性） → 对比度（绕 0.5 缩放） → 亮度（偏移） → 灰度系数（gamma 幂变换）。
/// 最终 ushort[768]（R/G/B 各 256）写入显卡 LUT。
/// </summary>
internal static class GammaLut
{
    /// <summary>线性基线（即无任何校准的状态）。</summary>
    public static ushort[] Linear()
    {
        var lut = new ushort[768];
        for (int i = 0; i < 256; i++)
        {
            ushort v = (ushort)(i * 257); // 0..255 → 0..65535 线性映射
            lut[i] = v;
            lut[256 + i] = v;
            lut[512 + i] = v;
        }
        return lut;
    }

    /// <summary>
    /// 合成最终 LUT。
    /// baseRamp 为 ICC vcgt（可 null → 线性）；
    /// brightness / contrast ∈ [0, 100]，50 = 中性无变化；
    /// gammaX100 ∈ [50, 400]，即 gamma 0.50~4.00，100（=1.00）为中性，与 NVIDIA 面板一致。
    /// </summary>
    public static ushort[] Compose(ushort[]? baseRamp, int brightness, int contrast, int gammaX100)
    {
        // brightness: 0~100, 50=无变化. offset = (value-50)/100 => -0.5~+0.5
        double brightnessOffset = (brightness - 50) / 100.0;
        // contrast: 0~100, 50=无变化. factor = value/50 => 0~2
        double contrastFactor = contrast / 50.0;
        // gamma: 0.50~4.00. 输出 = x^(1/gamma)：gamma>1 提亮暗部，gamma<1 压暗
        double gamma = Math.Clamp(gammaX100, 50, 400) / 100.0;
        double invGamma = 1.0 / gamma;

        ushort[] b = baseRamp ?? Linear();
        if (b.Length < 768) b = Linear();

        var lut = new ushort[768];

        for (int i = 0; i < 256; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                double x = b[c * 256 + i] / 65535.0;
                x = (x - 0.5) * contrastFactor + 0.5;  // 对比度：绕中点缩放
                x += brightnessOffset;                 // 亮度：整体偏移
                x = Math.Clamp(x, 0.0, 1.0);
                lut[c * 256 + i] = (ushort)Math.Round(Math.Pow(x, invGamma) * 65535.0); // 灰度系数
            }
        }
        return lut;
    }

    /// <summary>把 LUT 写入指定显示器。</summary>
    public static bool Apply(string deviceName, ushort[] lut)
    {
        return Native.ApplyGammaRamp(deviceName, lut);
    }
}
