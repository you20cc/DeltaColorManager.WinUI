using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeltaColorManager.Core;

/// <summary>一块显示器的枚举信息（含 WCS 需要的 PnP 设备 ID）。</summary>
internal sealed record DisplayInfo(string DeviceName, string Description, string PnpId, uint Flags);

/// <summary>user32 / gdi32 / mscms 的 P/Invoke 与显示器枚举。</summary>
internal static class Native
{
    public const uint DISPLAY_DEVICE_ACTIVE = 0x00000001;
    public const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000002;

    public const uint WCS_SCOPE_CURRENT_USER = 1;
    public const uint CLASS_MONITOR = 0x6D6E7472; // 'mntr' fourcc（注意：是 DWORD，不是字符串）
    public const uint CPT_ICC = 0;               // COLORPROFILETYPE_ICC
    public const uint CPST_PERCEPTUAL = 3;       // COLORPROFILESUBTYPE_PERCEPTUAL（与验证脚本一致）

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string? lpszDriver, string lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] lpRamp);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InstallColorProfile(IntPtr pMachine, string pProfilePath);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WcsSetUsePerUserProfiles(string pDeviceName, uint deviceType, bool usePerUser);

    [DllImport("mscms.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WcsSetCalibrationManagementState(bool bIsEnabled);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WcsAssociateColorProfileWithDevice(IntPtr hWcs, string pProfileName, string pDeviceName);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WcsDisassociateColorProfileFromDevice(IntPtr hWcs, string pProfileName, string pDeviceName);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WcsSetDefaultColorProfile(uint scope, string pDeviceName, uint cptColorProfileType, uint cpstColorProfileSubType, uint dwProfileID, string pProfileName);

    /// <summary>枚举活动显示器（兼容 ACTIVE / ATTACHED_TO_DESKTOP 两种标志）。</summary>
    public static List<DisplayInfo> EnumerateMonitors()
    {
        var result = new List<DisplayInfo>();
        for (uint adapter = 0; adapter < 16; adapter++)
        {
            var dd = NewDisplayDevice();
            if (!EnumDisplayDevices(null, adapter, ref dd, 0)) break;
            if ((dd.StateFlags & (DISPLAY_DEVICE_ACTIVE | DISPLAY_DEVICE_ATTACHED_TO_DESKTOP)) == 0) continue;

            var monitor = NewDisplayDevice();
            if (!EnumDisplayDevices(dd.DeviceName, 0, ref monitor, 0)) continue;

            result.Add(new DisplayInfo(dd.DeviceName, monitor.DeviceString, monitor.DeviceID, dd.StateFlags));
        }
        return result;
    }

    private static DisplayDevice NewDisplayDevice()
    {
        var dd = new DisplayDevice
        {
            DeviceName = string.Empty,
            DeviceString = string.Empty,
            DeviceID = string.Empty,
            DeviceKey = string.Empty,
            cb = 840, // DISPLAY_DEVICEW 固定大小
        };
        return dd;
    }

    /// <summary>把 768 项（R/G/B 各 256）伽马表写入指定显示器的显卡 LUT。</summary>
    public static bool ApplyGammaRamp(string deviceName, ushort[] ramp)
    {
        IntPtr hdc = CreateDC(null, deviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try
        {
            return SetDeviceGammaRamp(hdc, ramp);
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    /// <summary>写诊断日志到 %AppData%\DeltaColorManager\apply.log。</summary>
    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeltaColorManager");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "apply.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// 调用 mscms 完整链路：安装 → 启用“使用我对此设备的设置” → 关联 → 设为默认。
    /// 注册完成后，若 ICC 含 vcgt 则直接写入显卡 LUT（屏幕立即变色）；
    /// 若不含 vcgt 则重启系统校准加载器让 WCS 自行生效。
    /// 返回 (错误信息, vcgt ramp)。错误信息为 null 表示成功；vcgt 为 null 表示该 ICC 无 vcgt 标签。
    /// </summary>
    public static (string? error, ushort[]? vcgt) ApplyIccProfile(string fileName, string pnpId, string deviceName)
    {
        string colorDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");
        string fullPath = Path.Combine(colorDir, fileName);
        if (!File.Exists(fullPath)) return ($"文件不存在: {fullPath}", null);
        if (string.IsNullOrEmpty(pnpId)) return ("无 PnP ID（显示器枚举失败）", null);

        try
        {
            bool b1 = InstallColorProfile(IntPtr.Zero, fullPath);
            Log($"WCS1 InstallColorProfile({fullPath}) = {b1}, err={Marshal.GetLastWin32Error()}");
            if (!b1)
                return ($"安装失败 (err={Marshal.GetLastWin32Error()})", null);

            bool b2 = WcsSetUsePerUserProfiles(pnpId, CLASS_MONITOR, true);
            Log($"WCS2 WcsSetUsePerUserProfiles({pnpId}, 0x{CLASS_MONITOR:X}) = {b2}, err={Marshal.GetLastWin32Error()}");
            if (!b2)
                return ($"启用“使用我对此设备的设置”失败 (err={Marshal.GetLastWin32Error()})", null);

            bool b3 = WcsAssociateColorProfileWithDevice(IntPtr.Zero, fileName, pnpId);
            Log($"WCS3 WcsAssociateColorProfileWithDevice({fileName}) = {b3}, err={Marshal.GetLastWin32Error()}");
            if (!b3)
                return ($"关联到设备失败 (err={Marshal.GetLastWin32Error()})", null);

            bool b4 = WcsSetDefaultColorProfile(WCS_SCOPE_CURRENT_USER, pnpId, CPT_ICC, CPST_PERCEPTUAL, 0, fileName);
            Log($"WCS4 WcsSetDefaultColorProfile = {b4}, err={Marshal.GetLastWin32Error()}");
            if (!b4)
                return ($"设为默认失败 (err={Marshal.GetLastWin32Error()})", null);

            // 解析 vcgt 并立即生效（参考 filter-manage-main icc.rs:407-458）
            ushort[]? vcgt = IccManager.ParseVcgt(fileName);
            if (vcgt != null && vcgt.Length == 768)
            {
                bool rampOk = ApplyGammaRamp(deviceName, vcgt);
                Log($"vcgt 直接写入 LUT({deviceName}) = {rampOk}, [64]R={vcgt[64]}, [128]R={vcgt[128]}");
            }
            else
            {
                Log("该 ICC 无 vcgt 标签 → 重启校准加载器");
                // 无 vcgt：重启校准加载器，让 WCS 自行应用 profile
                TriggerCalibrationReload();
            }
            return (null, vcgt);
        }
        catch (Exception ex)
        {
            Log($"ApplyIccProfile 异常: {ex}");
            return ($"异常: {ex.Message}", null);
        }
    }

    /// <summary>
    /// 恢复默认：切到程序自生成的「DCM Default.icm」（默认状态 ICC，identity vcgt）。
    /// 不依赖系统里是否有 sRGB Color Space Profile.icm（那是第三方校色软件装的，别的机器没有）。
    /// 流程：确保 DCM Default.icm 存在（首次生成需管理员）→ 解除所有旧关联 → 关联它并设为默认。
    /// 返回 null = 成功；否则返回错误信息。
    /// </summary>
    public static string? ClearIccAssociation(string pnpId, string deviceName, string? profileName)
    {
        _ = profileName; // 不再需要（DCM Default 关联会覆盖默认）
        if (string.IsNullOrEmpty(pnpId)) return "无 PnP ID（显示器枚举失败）";
        try
        {
            // 1) 确保 DCM Default.icm 已生成并装进系统颜色目录
            string? fileName = EnsureDefaultIcc();
            if (fileName == null)
                return "生成默认 ICC 失败：请右键以管理员身份运行本程序一次（生成 DCM Default.icm），之后无需管理员";
            Log($"RestoreDefault: 使用程序生成的默认 ICC = {fileName}");

            // 2) 解除当前 pnpId 下所有已关联的 ICC（WCS API + HKCU 注册表），
            //    关联列表里就只剩 DCM Default，干净。
            var allProfiles = GetAssociatedProfilesFromRegistry(pnpId);
            Log($"RestoreDefault: {pnpId} 当前关联 ICC 数: {allProfiles.Count} -> [{string.Join(", ", allProfiles)}]");
            foreach (var prof in allProfiles)
            {
                bool b = WcsDisassociateColorProfileFromDevice(IntPtr.Zero, prof, pnpId);
                Log($"RestoreDefault: Disassociate({prof}) = {b}, err={Marshal.GetLastWin32Error()}");
            }
            ClearUserRegistryAssociations(pnpId);

            // 3) 走完整 WCS 链路：关联 DCM Default + 设为默认 + 写 identity vcgt LUT
            var (err, _) = ApplyIccProfile(fileName, pnpId, deviceName);
            if (err != null)
            {
                Log($"RestoreDefault: ApplyIccProfile 失败: {err}");
                return err;
            }

            Log("RestoreDefault: 成功，关联列表已切到 DCM Default");
            return null;
        }
        catch (Exception ex)
        {
            Log($"ClearIccAssociation 异常: {ex}");
            return $"异常: {ex.Message}";
        }
    }

    /// <summary>
    /// 确保「DCM Default.icm」存在于系统颜色目录：已存在直接返回文件名；
    /// 不存在则生成（sRGB 基色 + identity vcgt 的最小 ICC v2.1 显示器 profile），
    /// 先写临时目录再用 InstallColorProfile 装进系统目录（写系统目录需管理员权限）。
    /// 返回文件名；失败返回 null（详见 apply.log）。
    /// </summary>
    public static string? EnsureDefaultIcc()
    {
        const string fileName = "DCM Default.icm";
        string colorDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");
        string destPath = Path.Combine(colorDir, fileName);
        try
        {
            if (File.Exists(destPath))
            {
                Log($"EnsureDefaultIcc: {destPath} 已存在，直接复用");
                return fileName;
            }

            byte[] icc = BuildDefaultIccBytes();
            string tmpPath = Path.Combine(Path.GetTempPath(), fileName);
            File.WriteAllBytes(tmpPath, icc);
            Log($"EnsureDefaultIcc: 已生成临时文件 {tmpPath} ({icc.Length} bytes)");

            if (!InstallColorProfile(IntPtr.Zero, tmpPath))
            {
                int err = Marshal.GetLastWin32Error();
                Log($"EnsureDefaultIcc: InstallColorProfile 失败 err={err}");
                return null;
            }
            Log($"EnsureDefaultIcc: 已安装到 {destPath}");
            return fileName;
        }
        catch (Exception ex)
        {
            Log($"EnsureDefaultIcc 异常: {ex}");
            return null;
        }
    }

    /// <summary>
    /// 构造「DCM Default.icm」的字节：真·identity ICC v2.1 profile(对 WCS 与 GPU LUT 都是 no-op)。
    /// - TRC = gamma 1.0(线性) + 单位矩阵 → WCS 不做曲线变换(否则现代应用 Direct2D/Chromium 主画布会被压黑)
    /// - vcgt = identity 表(3×256,值 = i*257) → 显卡 LUT 回到纯线性
    /// - 不要写 sRGB 风格的 gamma 2.2 + sRGB 基色:显示器本身已是 sRGB,再套一份 sRGB profile 会 double-gamma
    /// </summary>
    private static byte[] BuildDefaultIccBytes()
    {
        static byte[] U32BE(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        static byte[] U16BE(ushort v) => [(byte)(v >> 8), (byte)v];
        static byte[] Sig(string s) => System.Text.Encoding.ASCII.GetBytes(s);
        static uint Fix(double v) => (uint)Math.Round(v * 65536.0);

        byte[] XYZTag(double x, double y, double z)
        {
            var b = new List<byte>();
            b.AddRange(Sig("XYZ "));
            b.AddRange(new byte[4]);
            b.AddRange(U32BE(Fix(x)));
            b.AddRange(U32BE(Fix(y)));
            b.AddRange(U32BE(Fix(z)));
            return b.ToArray();
        }

        byte[] CurveTag(double gamma)
        {
            var b = new List<byte>();
            b.AddRange(Sig("curv"));
            b.AddRange(new byte[4]);
            b.AddRange(U32BE(1));              // count=1 → 单值 gamma
            b.AddRange(U32BE(Fix(gamma)));     // u16Fixed16
            return b.ToArray();
        }

        byte[] DescTag(string s)
        {
            var b = new List<byte>();
            byte[] ascii = System.Text.Encoding.ASCII.GetBytes(s);
            b.AddRange(Sig("desc"));
            b.AddRange(new byte[4]);
            b.AddRange(U32BE((uint)(ascii.Length + 1)));
            b.AddRange(ascii);
            b.Add(0);
            b.AddRange(U32BE(0));   // unicode language code
            b.AddRange(U32BE(0));   // unicode count
            b.AddRange(new byte[2]);// scriptcode code
            b.Add(0);               // scriptcode count
            b.AddRange(new byte[67]);
            return b.ToArray();
        }

        byte[] TextTag(string s)
        {
            var b = new List<byte>();
            byte[] ascii = System.Text.Encoding.ASCII.GetBytes(s);
            b.AddRange(Sig("text"));
            b.AddRange(new byte[4]);
            b.AddRange(ascii);
            b.Add(0);
            return b.ToArray();
        }

        byte[] VcgtIdentityTag()
        {
            var b = new List<byte>();
            b.AddRange(Sig("vcgt"));
            b.AddRange(new byte[4]);
            b.AddRange(U32BE(0));      // type 0 = table
            b.AddRange(U16BE(3));      // channels
            b.AddRange(U16BE(256));    // entries
            b.AddRange(U16BE(2));      // entry size (bytes)
            for (int ch = 0; ch < 3; ch++)
                for (int i = 0; i < 256; i++)
                    b.AddRange(U16BE((ushort)(i * 257))); // identity: i/255*65535
            return b.ToArray();
        }

        // 单位矩阵 + gamma 1.0 = 完整 identity 转换(WCS 不会引入任何颜色变换)
        var tagList = new List<(string sig, byte[] data)>
        {
            ("desc", DescTag("DCM Default")),
            ("wtpt", XYZTag(0.9642, 1.0000, 0.8249)),      // D50
            ("rXYZ", XYZTag(1.0, 0.0, 0.0)),                // 单位矩阵 R
            ("gXYZ", XYZTag(0.0, 1.0, 0.0)),                // 单位矩阵 G
            ("bXYZ", XYZTag(0.0, 0.0, 1.0)),                // 单位矩阵 B
            ("rTRC", CurveTag(1.0)),
            ("gTRC", CurveTag(1.0)),
            ("bTRC", CurveTag(1.0)),
            ("cprt", TextTag("DeltaColorManager default identity profile")),
            ("vcgt", VcgtIdentityTag()),
        };

        // 布局：header(128) + count(4) + table(n*12) + tag 数据（各 tag 4 字节对齐）
        int dataStart = 128 + 4 + tagList.Count * 12; // 天然 4 对齐
        var offsets = new List<int>();
        var sizes = new List<int>();
        int cur = dataStart;
        foreach (var (_, data) in tagList)
        {
            offsets.Add(cur);
            sizes.Add(data.Length);
            cur += data.Length;
            cur += (4 - cur % 4) % 4; // pad to 4
        }
        int totalSize = cur;

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);

        // ---- Header (128 bytes) ----
        w.Write(U32BE((uint)totalSize));   // 0:  profile size
        w.Write(Sig("DCM "));              // 4:  CMM
        w.Write(U32BE(0x02100000));        // 8:  version 2.1
        w.Write(Sig("mntr"));              // 12: device class = display
        w.Write(Sig("RGB "));              // 16: color space
        w.Write(Sig("XYZ "));              // 20: PCS
        w.Write(U16BE(2026)); w.Write(U16BE(8)); w.Write(U16BE(30)); // 24: date
        w.Write(U16BE(0)); w.Write(U16BE(0)); w.Write(U16BE(0));     //      time
        w.Write(Sig("acsp"));              // 36: signature
        w.Write(Sig("MSFT"));              // 40: platform（4 字节，'Micro' 是 5 字节会破坏布局！）
        w.Write(U32BE(0));                 // 44: flags
        w.Write(Sig("DCM "));              // 48: manufacturer
        w.Write(U32BE(0));                 // 52: model
        w.Write(U32BE(0)); w.Write(U32BE(0)); // 56: attributes (uint64)
        w.Write(U32BE(0));                 // 64: rendering intent = perceptual
        w.Write(U32BE(Fix(0.9642)));       // 68: D50 illuminant X
        w.Write(U32BE(Fix(1.0000)));       // 72: Y
        w.Write(U32BE(Fix(0.8249)));       // 76: Z
        w.Write(Sig("DCM "));              // 80: creator
        w.Write(new byte[16]);             // 84: profile ID
        w.Write(new byte[28]);             // 100: reserved

        // ---- Tag table ----
        w.Write(U32BE((uint)tagList.Count));
        for (int i = 0; i < tagList.Count; i++)
        {
            w.Write(Sig(tagList[i].sig));
            w.Write(U32BE((uint)offsets[i]));
            w.Write(U32BE((uint)sizes[i]));
        }

        // ---- Tag data（含 4 字节对齐 padding）----
        for (int i = 0; i < tagList.Count; i++)
        {
            w.Write(tagList[i].data);
            int pad = (4 - sizes[i] % 4) % 4;
            for (int p = 0; p < pad; p++) w.Write((byte)0);
        }

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>读取 HKCU 注册表里该 pnpId 下所有已关联的 ICC 文件名（开机自动加载的源头）。</summary>
    private static List<string> GetAssociatedProfilesFromRegistry(string pnpId)
    {
        var result = new List<string>();
        try
        {
            string path = $@"Software\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display\{pnpId}";
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            if (key != null)
            {
                foreach (var sub in key.GetSubKeyNames())
                    if (!string.IsNullOrEmpty(sub)) result.Add(sub);
            }
        }
        catch (Exception ex)
        {
            Log($"GetAssociatedProfilesFromRegistry 异常: {ex.Message}");
        }
        return result;
    }

    /// <summary>清空 HKCU 下该 pnpId 的 ProfileAssociations 和 DefaultAssociations 两个注册表位置。返回是否清干净。</summary>
    private static bool ClearUserRegistryAssociations(string pnpId)
    {
        bool anyCleared = false;
        try
        {
            string[] paths =
            {
                $@"Software\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display\{pnpId}",
                $@"Software\Microsoft\Windows NT\CurrentVersion\ICM\DefaultAssociations\Display\{pnpId}",
            };
            foreach (var path in paths)
            {
                using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
                if (key == null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        Registry.CurrentUser.DeleteSubKeyTree($@"{path}\{sub}", throwOnMissingSubKey: false);
                        Log($"ClearReg: 删除 {path}\\{sub}");
                        anyCleared = true;
                    }
                    catch (Exception ex)
                    {
                        Log($"ClearReg: 删除 {path}\\{sub} 失败: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"ClearUserRegistryAssociations 异常: {ex.Message}");
        }
        return anyCleared;
    }

    /// <summary>检查 HKLM 系统级注册表里该 pnpId 下还有没有 ICC 关联（仅读不写，需要管理员权限才能清）。</summary>
    private static bool HasSystemRegistryAssociations(string pnpId)
    {
        try
        {
            string[] paths =
            {
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display\{pnpId}",
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM\DefaultAssociations\Display\{pnpId}",
            };
            foreach (var path in paths)
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
                if (key != null && key.GetSubKeyNames().Length > 0)
                    return true;
            }
        }
        catch (Exception ex)
        {
            Log($"HasSystemRegistryAssociations 异常: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// 把任意路径的 ICC/ICM 文件安装到系统颜色目录（C:\Windows\System32\spool\drivers\color）。
    /// InstallColorProfile 会自动复制文件；写入系统目录需要管理员权限。
    /// 返回 null = 成功；否则返回错误信息。
    /// </summary>
    public static string? ImportIccFile(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath)) return $"文件不存在: {sourcePath}";
            Log($"ImportIccFile({sourcePath})");
            if (!InstallColorProfile(IntPtr.Zero, sourcePath))
            {
                int err = Marshal.GetLastWin32Error();
                Log($"ImportIccFile 失败 err={err}");
                return err == 5
                    ? "权限不足：写入系统颜色目录需要管理员权限，请右键以管理员身份运行程序后重试"
                    : $"安装失败 (err={err})";
            }
            Log("ImportIccFile 成功");
            return null;
        }
        catch (Exception ex)
        {
            Log($"ImportIccFile 异常: {ex.Message}");
            return $"异常: {ex.Message}";
        }
    }

    /// <summary>
    /// 重启系统校准加载器（off→on），让没有 vcgt 标签的 ICC 也能立即生效。
    /// 对应验证脚本 icc-switch.py 的第 5 步降级路径。
    /// </summary>
    public static void TriggerCalibrationReload()
    {
        WcsSetCalibrationManagementState(false);
        WcsSetCalibrationManagementState(true);
    }
}
