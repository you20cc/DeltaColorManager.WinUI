using DeltaColorManager.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DeltaColorManager;

public sealed partial class MainWindow : Window
{
    private const string AppVersion = "2.0";

    // ---- 状态(与 WinForms 版 MainForm 一致) ----
    private readonly Dictionary<string, ushort[]?> _vcgtCache = new(StringComparer.OrdinalIgnoreCase);
    private List<DisplayInfo> _displays = new();
    private List<Profile> _profiles = new();
    private bool _suppressIccEvent;    // 方案应用期间防止 ICC 事件重复触发全链路
    private bool _suppressSliderEvent; // 程序化改滑条(初始化/应用方案/恢复默认)时不触发应用
    private int _lastIccIndex = -1;    // 上次 ICC 下拉索引:区分「初始加载」和「从 ICC 切回首项」

    public MainWindow()
    {
        InitializeComponent();

        // WinUI 的 XAML 编译器不保证按书写顺序应用 Min/Max/Value,
        // Value>10 时若默认范围(0-10)尚未被覆盖会抛 XamlParseException → 必须代码里设
        _suppressSliderEvent = true;
        try
        {
            SldBrightness.Minimum = 0; SldBrightness.Maximum = 100; SldBrightness.Value = 50;
            SldContrast.Minimum = 0; SldContrast.Maximum = 100; SldContrast.Value = 50;
            SldGrayscale.Minimum = 50; SldGrayscale.Maximum = 400; SldGrayscale.Value = 100;
            SldVibrance.Minimum = 0; SldVibrance.Maximum = 100; SldVibrance.Value = 50;
            UpdateLabels();
        }
        finally { _suppressSliderEvent = false; }

        // 自定义标题栏(WinUI 自带深色,无需再调 DwmSetWindowAttribute)
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(700, 940));

        // 标题栏区域会被系统注册为拖拽区,里面的交互控件收不到点击;
        // 必须把开关所占矩形声明为「穿透区」(passthrough)才能正常交互
        ThemeSwitch.Loaded += (_, _) => UpdateTitleBarPassthrough();
        ThemeSwitch.SizeChanged += (_, _) => UpdateTitleBarPassthrough();

        // 居中:WinUI 没有直接属性,按显示器工作区手动算坐标
        CenterWindow();

        // 读取上次主题:赋值 IsOn 只有「值变化」才触发 Toggled(存浅色时 false→false 不触发),
        // 所以必须显式调一次 ApplyTheme 兜底
        bool dark = LoadThemeDark();
        ThemeSwitch.IsOn = dark;
        ApplyTheme(dark);

        // Window 没有 Loaded 事件:用 Activated 首次激活代替 Form.Load
        Activated += OnWindowActivated;
    }

    /// <summary>把主题开关的矩形从标题栏拖拽区里挖出来,让点击落到控件上(随布局/缩放更新)。</summary>
    private void UpdateTitleBarPassthrough()
    {
        try
        {
            if (Content?.XamlRoot is null || ThemeSwitch.ActualWidth == 0) return;
            double scale = Content.XamlRoot.RasterizationScale;
            Windows.Foundation.Point pos =
                ThemeSwitch.TransformToVisual(Content).TransformPoint(new Windows.Foundation.Point(0, 0));
            var rect = new Windows.Graphics.RectInt32(
                (int)Math.Round(pos.X * scale), (int)Math.Round(pos.Y * scale),
                (int)Math.Round(ThemeSwitch.ActualWidth * scale), (int)Math.Round(ThemeSwitch.ActualHeight * scale));
            var src = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            src.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, new[] { rect });
        }
        catch { /* 布局未就绪等异常忽略,下次事件重试 */ }
    }

    private void CenterWindow()
    {
        // 在窗口所在显示器的工作区内居中(避开任务栏)
        Microsoft.UI.Windowing.DisplayArea area =
            Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(
            work.X + (work.Width - 700) / 2,
            work.Y + Math.Max(0, (work.Height - 940) / 2)));
    }

    // ================= 主题切换(持久化到 %AppData%\DeltaColorManager\settings.json) =================

    private static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeltaColorManager");

    private static bool LoadThemeDark()
    {
        try
        {
            string path = Path.Combine(SettingsDir, "settings.json");
            if (!File.Exists(path)) return true;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return !doc.RootElement.TryGetProperty("Theme", out var t) || t.GetString() != "Light";
        }
        catch { return true; }
    }

    private static void SaveThemeDark(bool dark)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(Path.Combine(SettingsDir, "settings.json"),
                $"{{ \"Theme\": \"{(dark ? "Dark" : "Light")}\" }}");
        }
        catch { /* 偏好写失败不影响主流程 */ }
    }

    private void ThemeSwitch_Toggled(object sender, RoutedEventArgs e) => ApplyTheme(ThemeSwitch.IsOn);

    private void ApplyTheme(bool dark)
    {
        if (Content is FrameworkElement root)
            root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;

        // 标题栏的最小化/最大化/关闭按钮不跟随 XAML 主题,要按 AppWindow.TitleBar 手动配色
        var fg = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        var tb = AppWindow.TitleBar;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedForegroundColor = fg;
        tb.ButtonInactiveForegroundColor = fg;

        SaveThemeDark(dark);
    }

    private bool _initialized;

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialized || args.WindowActivationState == WindowActivationState.Deactivated) return;
        _initialized = true;
        Activated -= OnWindowActivated; // 只执行一次
        OnLoaded(this, new RoutedEventArgs());
    }

    /// <summary>写诊断日志到 %AppData%\DeltaColorManager\apply.log(追加式,与 WinForms 版同一文件)。</summary>
    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeltaColorManager");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "apply.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { /* 日志失败不影响主流程 */ }
    }

    // ================= 加载 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _suppressSliderEvent = true;
        try
        {
            // 显示器
            _displays = Native.EnumerateMonitors();
            foreach (DisplayInfo d in _displays)
            {
                CmbDisplay.Items.Add($"{d.DeviceName}  {d.Description}");
            }
            if (CmbDisplay.Items.Count > 0) CmbDisplay.SelectedIndex = 0;

            // ICC 列表(首项 = 无滤镜,选中即取消)
            RefreshIccList();

            // 数字振动
            if (NvApi.Available)
            {
                SldVibrance.Minimum = Math.Max(0, NvApi.Min);
                SldVibrance.Maximum = Math.Min(100, NvApi.Max);
                SldVibrance.Value = Math.Clamp(NvApi.Get(), (int)SldVibrance.Minimum, (int)SldVibrance.Maximum);
                SetStatus($"数字振动就绪 (range {NvApi.Min}-{NvApi.Max}, default {NvApi.Default})");
            }
            else
            {
                SldVibrance.IsEnabled = false;
                SetStatus("未检测到 NVIDIA 驱动(数字振动不可用)");
            }

            // 方案
            _profiles = ProfileStore.Load();
            RefreshProfileList();
            UpdateLabels();
        }
        finally { _suppressSliderEvent = false; }
    }

    // ================= 核心应用逻辑 =================

    private DisplayInfo? CurrentDisplay =>
        CmbDisplay.SelectedIndex >= 0 && CmbDisplay.SelectedIndex < _displays.Count
            ? _displays[CmbDisplay.SelectedIndex]
            : null;

    private ushort[]? GetVcgtBase()
    {
        if (CmbIcc.SelectedIndex <= 0) return null;
        string icc = (string)CmbIcc.SelectedItem!;
        if (!_vcgtCache.TryGetValue(icc, out ushort[]? vcgt))
        {
            vcgt = IccManager.ParseVcgt(icc);
            _vcgtCache[icc] = vcgt;
        }
        return vcgt;
    }

    /// <summary>应用当前画面调节(亮度/对比度/灰度,叠加 ICC 的 vcgt 基线)到显卡 LUT。</summary>
    private bool ApplyCurrentLut()
    {
        DisplayInfo? dev = CurrentDisplay;
        if (dev == null) { Log("ApplyCurrentLut: dev=null,直接失败"); return false; }
        ushort[]? base_ = GetVcgtBase();
        ushort[] lut = GammaLut.Compose(base_,
            (int)SldBrightness.Value, (int)SldContrast.Value, (int)SldGrayscale.Value);
        bool ok = GammaLut.Apply(dev.DeviceName, lut);
        Log($"ApplyCurrentLut: base={(base_ == null ? "线性" : $"vcgt([64]R={base_[64]})")}, 写入LUT[64]R={lut[64]}, [128]R={lut[128]}, [192]R={lut[192]}, Apply={ok}");
        if (!ok)
            SetStatus($"LUT 应用失败({dev.DeviceName} 可能是虚拟显示器,GammaRamp 不支持)");
        return ok;
    }

    /// <summary>应用全部:ICC 关联(若选择)→ LUT → 数字振动。失败会在状态栏显示具体原因。</summary>
    private void ApplyAll()
    {
        DisplayInfo? dev = CurrentDisplay;

        Log($"=== ApplyAll v{AppVersion} 开始 ===");
        Log($"显示器下拉: SelectedIndex={CmbDisplay.SelectedIndex}, 共 {_displays.Count} 台");
        Log($"目标设备: {(dev == null ? "null(CurrentDisplay 为空)" : $"{dev.DeviceName} | {dev.Description} | PnP={dev.PnpId}")}");
        Log($"ICC 下拉: SelectedIndex={CmbIcc.SelectedIndex}, 选中={CmbIcc.SelectedItem ?? "(无)"}");
        Log($"滑条: 亮度={(int)SldBrightness.Value} 对比度={(int)SldContrast.Value} 灰度={(int)SldGrayscale.Value} 振动={(int)SldVibrance.Value}");
        Log($"NvApi: Available={NvApi.Available}");

        // 1. ICC 完整链路(WCS 注册 + 立即写 LUT,参考 filter-manage-main icc.rs)
        string iccName = "";
        string iccError = "";
        ushort[]? iccVcgt = null;
        if (CmbIcc.SelectedIndex > 0 && dev != null)
        {
            iccName = (string)CmbIcc.SelectedItem!;
            var (err, vcgt) = Native.ApplyIccProfile(iccName, dev.PnpId, dev.DeviceName);
            iccError = err ?? "";
            iccVcgt = vcgt;
            Log($"ICC ApplyIccProfile: err=\"{iccError}\", vcgt={(vcgt == null ? "无" : $"有(768项, [64]R={vcgt[64]}, [128]R={vcgt[128]})")}");
            if (vcgt != null)
                _vcgtCache[iccName] = vcgt;   // 更新缓存,后续 LUT 合成以此为基础
        }
        else
        {
            if (CmbIcc.SelectedIndex <= 0 && dev != null)
            {
                // 无 ICC 滤镜:WCS 同步切到 DCM Default(与下拉切回首项的行为一致)
                string? clearErr = Native.ClearIccAssociation(dev.PnpId, dev.DeviceName, null);
                Log($"无ICC → ClearIccAssociation: {clearErr ?? "成功(已切 DCM Default)"}");
            }
            else
            {
                Log($"ICC 跳过: {(CmbIcc.SelectedIndex <= 0 ? "未选择 ICC(SelectedIndex=0)" : "无目标设备")}");
            }
        }

        // 2. LUT(在 ICC vcgt 基线上叠加亮度/对比度/灰度)
        bool lutOk = ApplyCurrentLut();
        Log($"LUT ApplyCurrentLut: ok={lutOk}");

        // 3. 数字振动
        bool dvcOk = !NvApi.Available || NvApi.Set((int)SldVibrance.Value);
        Log($"振动 NvApi.Set: ok={dvcOk}");

        string status = lutOk ? "已应用" : "LUT 应用失败";
        if (NvApi.Available) status += dvcOk ? ",振动✓" : ",振动✗";
        if (iccName.Length > 0)
            status += iccError.Length == 0 ? $",ICC「{iccName}」✓" : $",ICC✗ {iccError}";
        SetStatus(status);
        Log($"状态栏: {status}");
    }

    // ================= 事件 =================

    private void LutSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateLabels();
        if (_suppressSliderEvent) return;
        ApplyCurrentLut();
    }

    private void VibranceSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateLabels();
        if (_suppressSliderEvent) return;
        if (NvApi.Available)
        {
            bool ok = NvApi.Set((int)SldVibrance.Value);
            SetStatus(ok ? $"数字振动: + {(int)SldVibrance.Value}%" : "数字振动设置失败");
        }
    }

    /// <summary>
    /// ICC 下拉选中即应用:选具体 ICC → 全链路生效;从 ICC 切回首项(无滤镜)→
    /// WCS 同步切到 DCM Default(没有则创建),LUT 写线性基线 + 当前滑条值。
    /// 启动时的初始选中(-1→0)不动作。
    /// </summary>
    private void CmbIcc_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx = CmbIcc.SelectedIndex;
        if (_suppressIccEvent) { _lastIccIndex = idx; return; }

        bool wasIcc = _lastIccIndex > 0; // 之前选的是具体 ICC
        _lastIccIndex = idx;

        if (idx <= 0)
        {
            if (!wasIcc) return; // 初始加载 / 恢复默认改索引 → 不动作
            Log("=== ICC 下拉切回首项 → 取消滤镜(WCS 切 DCM Default + 线性基线) ===");
            DisplayInfo? dev = CurrentDisplay;
            string? clearErr = dev == null ? "无目标设备" : Native.ClearIccAssociation(dev.PnpId, dev.DeviceName, null);
            if (clearErr != null) Log($"切回首项 ClearIccAssociation: {clearErr}");
            bool ok = ApplyCurrentLut();
            Log($"取消滤镜: LUT={ok}, WCS切换={(clearErr == null ? "成功" : clearErr)}");
            SetStatus(clearErr == null
                ? (ok ? "已取消 ICC 滤镜(系统已切到 DCM Default)" : "LUT 应用失败")
                : $"已取消滤镜(ICC 切换失败:{clearErr})");
            return;
        }

        Log("=== ICC 下拉选中 → 立即应用 ===");
        ApplyAll();
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        string? name = await PromptAsync("保存方案", "方案名称:", $"方案 {_profiles.Count + 1}");
        if (string.IsNullOrEmpty(name)) return;

        // 同名覆盖
        _profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _profiles.Add(new Profile
        {
            Name = name,
            Brightness = (int)SldBrightness.Value,
            Contrast = (int)SldContrast.Value,
            Grayscale = (int)SldGrayscale.Value,
            Vibrance = (int)SldVibrance.Value,
            IccProfile = CmbIcc.SelectedIndex > 0 ? (string)CmbIcc.SelectedItem! : null,
        });
        ProfileStore.Save(_profiles);
        RefreshProfileList();
        LstProfiles.SelectedIndex = LstProfiles.Items.Count - 1;
        SetStatus($"已保存方案:{name}");
    }

    private void BtnApplyProfile_Click(object sender, RoutedEventArgs e) => ApplySelectedProfile();

    private void LstProfiles_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ApplySelectedProfile();

    private void ApplySelectedProfile()
    {
        if (LstProfiles.SelectedIndex < 0 || LstProfiles.SelectedIndex >= _profiles.Count) return;
        Profile p = _profiles[LstProfiles.SelectedIndex];

        _suppressSliderEvent = true;
        try
        {
            SldBrightness.Value = Math.Clamp(p.Brightness, (int)SldBrightness.Minimum, (int)SldBrightness.Maximum);
            SldContrast.Value = Math.Clamp(p.Contrast, (int)SldContrast.Minimum, (int)SldContrast.Maximum);
            SldGrayscale.Value = Math.Clamp(p.Grayscale, (int)SldGrayscale.Minimum, (int)SldGrayscale.Maximum);
            if (NvApi.Available) SldVibrance.Value = Math.Clamp(p.Vibrance, (int)SldVibrance.Minimum, (int)SldVibrance.Maximum);
        }
        finally { _suppressSliderEvent = false; }

        // 改下拉会触发 SelectionChanged → 抑制,由最后的 ApplyAll 统一应用
        _suppressIccEvent = true;
        try
        {
            int idx = p.IccProfile == null ? 0 : CmbIcc.Items.IndexOf(p.IccProfile);
            CmbIcc.SelectedIndex = idx < 0 ? 0 : idx;
        }
        finally { _suppressIccEvent = false; }

        UpdateLabels();
        ApplyAll();
        SetStatus($"已应用方案:{p.Name}");
    }

    private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (LstProfiles.SelectedIndex < 0 || LstProfiles.SelectedIndex >= _profiles.Count) return;
        Profile p = _profiles[LstProfiles.SelectedIndex];
        _profiles.RemoveAt(LstProfiles.SelectedIndex);
        ProfileStore.Save(_profiles);
        RefreshProfileList();
        SetStatus($"已删除方案:{p.Name}");
    }

    // ================= 方案导出 / 导入 =================

    /// <summary>导出选中方案为 *.dcolor 单文件(JSON),可直接发给别人。</summary>
    private async void BtnExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (LstProfiles.SelectedIndex < 0 || LstProfiles.SelectedIndex >= _profiles.Count)
        {
            SetStatus("请先在列表中选中要导出的方案");
            return;
        }
        Profile p = _profiles[LstProfiles.SelectedIndex];

        var dlg = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = p.Name,
        };
        dlg.FileTypeChoices.Add("DeltaColor 方案", new List<string> { ".dcolor" });
        dlg.FileTypeChoices.Add("JSON 文件", new List<string> { ".json" });
        InitializePicker(dlg);

        StorageFile? file = await dlg.PickSaveFileAsync();
        if (file == null) return;
        try
        {
            ProfileStore.Export(p, file.Path);
            SetStatus($"已导出方案「{p.Name}」→ {file.Path}");
        }
        catch (Exception ex)
        {
            SetStatus($"导出失败:{ex.Message}");
        }
    }

    /// <summary>导入 *.dcolor 方案文件(可多选),同名覆盖、异名追加,不替换整个方案库。</summary>
    private async void BtnImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        dlg.FileTypeFilter.Add(".dcolor");
        dlg.FileTypeFilter.Add(".json");
        InitializePicker(dlg);

        IReadOnlyList<StorageFile> files = await dlg.PickMultipleFilesAsync();
        if (files.Count == 0) return;

        int added = 0;
        var failed = new List<string>();
        Profile? last = null;
        foreach (StorageFile file in files)
        {
            Profile? p = ProfileStore.LoadFromFile(file.Path);
            if (p == null || p.Name.Length == 0) { failed.Add(file.Name); continue; }
            _profiles.RemoveAll(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            _profiles.Add(p);
            added++;
            last = p;
        }

        if (added > 0)
        {
            ProfileStore.Save(_profiles);
            RefreshProfileList();
            int idx = last == null ? -1 : _profiles.IndexOf(last);
            if (idx >= 0) LstProfiles.SelectedIndex = idx;
        }

        string msg = $"已导入 {added} 个方案";
        if (failed.Count > 0) msg += $",{failed.Count} 个文件格式无效({string.Join("、", failed)})";
        msg += added > 0 && last != null && last.IccProfile != null
            ? $"。注意:方案引用的 ICC「{last.IccProfile}」需已导入本机才会生效"
            : "";
        SetStatus(msg);
    }

    // ================= ICC 导入 =================

    /// <summary>导入外部 ICC/ICM 文件到系统颜色目录,并刷新下拉列表(不自动应用)。</summary>
    private async void BtnImportIcc_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        dlg.FileTypeFilter.Add(".icc");
        dlg.FileTypeFilter.Add(".icm");
        InitializePicker(dlg);

        StorageFile? file = await dlg.PickSingleFileAsync();
        if (file == null) return;

        string? err = Native.ImportIccFile(file.Path);
        if (err != null)
        {
            Log($"导入 ICC 失败: {err}");
            await ShowMessageAsync("导入 ICC", $"导入失败:\n{err}");
            return;
        }

        RefreshIccList();
        SetStatus($"已导入 ICC「{file.Name}」,在下拉框中选择即可应用");
    }

    /// <summary>重建 ICC 下拉列表,保持当前选中项不变(列表重建期间抑制选中事件)。</summary>
    private void RefreshIccList()
    {
        string current = CmbIcc.SelectedIndex > 0 ? (string)CmbIcc.SelectedItem! : "";
        _suppressIccEvent = true;
        try
        {
            CmbIcc.Items.Clear();
            CmbIcc.Items.Add("(无 ICC 滤镜)");
            foreach (string p in IccManager.ListProfiles())
                CmbIcc.Items.Add(p);
            int idx = current.Length == 0 ? 0 : CmbIcc.Items.IndexOf(current);
            CmbIcc.SelectedIndex = idx < 0 ? 0 : idx;
            _lastIccIndex = CmbIcc.SelectedIndex;
        }
        finally { _suppressIccEvent = false; }
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        _suppressSliderEvent = true;
        try
        {
            SldBrightness.Value = 50;
            SldContrast.Value = 50;
            SldGrayscale.Value = 100;
            if (NvApi.Available) SldVibrance.Value = Math.Clamp(NvApi.Default, (int)SldVibrance.Minimum, (int)SldVibrance.Maximum);
        }
        finally { _suppressSliderEvent = false; }

        // 记下当前 ICC(仅日志用),切回首项前抑制事件以免重复触发取消滤镜
        string? currentIcc = CmbIcc.SelectedIndex > 0 ? (string)CmbIcc.SelectedItem! : null;
        _suppressIccEvent = true;
        try { CmbIcc.SelectedIndex = 0; }
        finally { _suppressIccEvent = false; }
        _lastIccIndex = 0;
        UpdateLabels();

        DisplayInfo? dev = CurrentDisplay;
        if (dev == null) { SetStatus("无目标设备"); return; }

        // 切到程序自生成的「DCM Default.icm」(identity vcgt):解除所有旧关联 + 关联它并设为默认
        string? err = Native.ClearIccAssociation(dev.PnpId, dev.DeviceName, currentIcc);
        if (err != null) Log($"恢复默认 ClearIccAssociation: {err}");

        // 兜底:写线性 LUT + 恢复数字振动
        GammaLut.Apply(dev.DeviceName, GammaLut.Linear());
        if (NvApi.Available) NvApi.Set(NvApi.Default);

        SetStatus(err == null
            ? "已恢复默认视觉(已切到 DCM Default,关联列表已清空,开机加载默认 ICC)"
            : $"已恢复默认视觉(ICC 切换失败:{err})");
    }

    private async void BtnDiag_Click(object sender, RoutedEventArgs e)
    {
        await ShowMessageAsync("NVAPI 诊断",
            $"NVAPI Available: {NvApi.Available}\n\n诊断详情:\n{NvApi.Diagnosis}");
    }

    // ================= 对话框(替代 WinForms 的 PromptForm / MessageBox) =================

    /// <summary>文本输入对话框(用于方案命名),取消/空输入返回 null。</summary>
    private async Task<string?> PromptAsync(string title, string label, string defaultText = "")
    {
        var box = new TextBox { Text = defaultText };
        box.SelectAll();
        var dlg = new ContentDialog
        {
            Title = title,
            XamlRoot = Content.XamlRoot,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = label },
                    box,
                },
            },
        };
        ContentDialogResult result = await dlg.ShowAsync();
        string text = box.Text.Trim();
        return result == ContentDialogResult.Primary && text.Length > 0 ? text : null;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            XamlRoot = Content.XamlRoot,
            CloseButtonText = "确定",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
        };
        await dlg.ShowAsync();
    }

    /// <summary>非打包 WinUI 3 应用必须给文件选择器绑定窗口句柄,否则抛异常。</summary>
    private void InitializePicker(object picker)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    // ================= 辅助 =================

    private void UpdateLabels()
    {
        LblBrightness.Text = $"+ {(int)SldBrightness.Value}%";
        LblContrast.Text = $"+ {(int)SldContrast.Value}%";
        LblGrayscale.Text = $"{SldGrayscale.Value / 100.0:F2}";
        LblVibrance.Text = $"+ {(int)SldVibrance.Value}%";
    }

    private void RefreshProfileList()
    {
        LstProfiles.Items.Clear();
        foreach (Profile p in _profiles)
        {
            LstProfiles.Items.Add(p.IccProfile is null ? p.Name : $"{p.Name}   [{p.IccProfile}]");
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
