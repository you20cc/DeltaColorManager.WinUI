using System.Text.Json;

namespace DeltaColorManager.Core;

/// <summary>一个保存的颜色方案。</summary>
internal sealed class Profile
{
    public string Name { get; set; } = "";
    public int Brightness { get; set; }
    public int Contrast { get; set; }
    public int Grayscale { get; set; }
    public int Vibrance { get; set; } = 50;
    /// <summary>关联的 ICC 文件名；null = 无 ICC 滤镜（应用方案时会取消滤镜）。</summary>
    public string? IccProfile { get; set; }
}

/// <summary>方案的 JSON 持久化，存于 %AppData%\DeltaColorManager\profiles.json。</summary>
internal static class ProfileStore
{
    private static string DirPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeltaColorManager");

    private static string FilePath => Path.Combine(DirPath, "profiles.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static List<Profile> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<Profile>();
            return JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(FilePath))
                   ?? new List<Profile>();
        }
        catch
        {
            return new List<Profile>();
        }
    }

    public static void Save(List<Profile> profiles)
    {
        Directory.CreateDirectory(DirPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(profiles, JsonOpts));
    }

    /// <summary>把单个方案导出为独立文件（*.dcolor，JSON 格式，可分享）。</summary>
    public static void Export(Profile profile, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOpts));

    /// <summary>从导出的方案文件读回单个方案；格式非法返回 null。</summary>
    public static Profile? LoadFromFile(string path)
    {
        try { return JsonSerializer.Deserialize<Profile>(File.ReadAllText(path)); }
        catch { return null; }
    }
}
