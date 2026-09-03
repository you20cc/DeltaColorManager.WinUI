using System.Runtime.InteropServices;

namespace DeltaColorManager.Core;

/// <summary>
/// NVIDIA 数字振动（Digital Vibrance）控制 —— 通过 nvapi64.dll 动态解析函数指针调用。
/// 注意：枚举失败/多卡场景统一回退到第一个可用显示句柄（本机验证只有 index 0）。
/// </summary>
internal static class NvApi
{
    private const uint ID_INITIALIZE = 0x0150E828;
    private const uint ID_ENUM_DISPLAY_HANDLE = 0x9ABDD40D;
    private const uint ID_GET_DVC_INFO_EX = 0x0E45002D;
    private const uint ID_SET_DVC_LEVEL_EX = 0x4A82C2B1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DvcInfoEx
    {
        public uint Version;
        public int CurrentLevel;
        public int MinLevel;
        public int MaxLevel;
        public int DefaultLevel;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvInitFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvEnumFn(uint index, out uint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvDvcFn(uint handle, uint outputId, ref DvcInfoEx info);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", ExactSpelling = true)]
    private static extern IntPtr QueryInterface(uint id);

    private static readonly uint _handle;
    private static readonly NvDvcFn? _getFn;
    private static readonly NvDvcFn? _setFn;
    private static readonly int _min = 0, _max = 100, _default = 50;
    private static readonly string _diag = "";

    public static bool Available => _handle != 0 && _setFn != null;
    public static int Min => _min;
    public static int Max => _max;
    public static int Default => _default;
    /// <summary>初始化诊断信息，用于排查数字振动为什么没反应。</summary>
    public static string Diagnosis => _diag;

    static NvApi()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            // 检查 nvapi64.dll 存在性
            string dllPath = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
            if (!File.Exists(dllPath))
            {
                _diag = "未找到 nvapi64.dll（非 NVIDIA 显卡或驱动未装）";
                return;
            }
            sb.AppendLine($"dll={dllPath}");

            var init = Marshal.GetDelegateForFunctionPointer<NvInitFn>(QueryInterface(ID_INITIALIZE));
            int st = init();
            sb.AppendLine($"Initialize()={st}");
            if (st != 0)
            {
                _diag = $"NVAPI 初始化失败 (status={st})。可能原因：远程桌面/虚拟显示器/驱动未加载。\n{sb}";
                return;
            }

            var enumFn = Marshal.GetDelegateForFunctionPointer<NvEnumFn>(QueryInterface(ID_ENUM_DISPLAY_HANDLE));
            uint foundHandle = 0;
            int enumCount = 0;
            for (uint i = 0; i < 16; i++)
            {
                int est = enumFn(i, out uint h);
                enumCount++;
                if (est == 0)
                {
                    if (foundHandle == 0) foundHandle = h; // 取第一个可用句柄
                    sb.AppendLine($"Enum[{i}] handle={h} OK");
                }
                else
                {
                    sb.AppendLine($"Enum[{i}] failed status={est}");
                    break;
                }
            }
            _handle = foundHandle;
            sb.AppendLine($"selected_handle={_handle}");
            if (_handle == 0)
            {
                _diag = $"未枚举到有效 NVAPI Display Handle（常见原因：GameViewer/远程桌面/Optimus 混合输出）。\n{sb}";
                return;
            }

            _getFn = Marshal.GetDelegateForFunctionPointer<NvDvcFn>(QueryInterface(ID_GET_DVC_INFO_EX));
            _setFn = Marshal.GetDelegateForFunctionPointer<NvDvcFn>(QueryInterface(ID_SET_DVC_LEVEL_EX));
            sb.AppendLine($"GetFn={_getFn != null} SetFn={_setFn != null}");

            var info = Query();
            if (info != null)
            {
                _min = info.Value.MinLevel;
                _max = info.Value.MaxLevel;
                _default = info.Value.DefaultLevel;
                sb.AppendLine($"DVC range=[{_min},{_max}] default={_default} current={info.Value.CurrentLevel}");
            }
            else
            {
                sb.AppendLine("Query() returned null");
            }
            _diag = sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            _diag = $"NVAPI 初始化异常：{ex.Message}\n{sb}";
        }
    }

    public static int Get()
    {
        var info = Query();
        return info?.CurrentLevel ?? 50;
    }

    public static bool Set(int level)
    {
        if (!Available) return false;
        var info = new DvcInfoEx
        {
            Version = (uint)(Marshal.SizeOf<DvcInfoEx>() | 0x10000),
            CurrentLevel = Math.Clamp(level, _min, _max),
            MinLevel = _min,
            MaxLevel = _max,
            DefaultLevel = _default,
        };
        return _setFn!(_handle, 0, ref info) == 0;
    }

    private static DvcInfoEx? Query()
    {
        if (_handle == 0 || _getFn == null) return null;
        var info = new DvcInfoEx
        {
            Version = (uint)(Marshal.SizeOf<DvcInfoEx>() | 0x10000),
            CurrentLevel = 50,
            MinLevel = 0,
            MaxLevel = 100,
            DefaultLevel = 50,
        };
        return _getFn(_handle, 0, ref info) == 0 ? info : null;
    }
}
