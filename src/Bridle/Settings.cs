using System.Text.Json;

namespace Bridle;

/// <summary>Oturumlar arasında hatırlanan durum: %APPDATA%\Bridle\settings.json.</summary>
internal sealed class Settings
{
    public List<string> Dlls { get; set; } = new();
    public string? SelectedA { get; set; }
    public string? SelectedB { get; set; }
    public string? LastFile { get; set; }
    public string Source { get; set; } = "";
    public string Inputs { get; set; } = "";
    public string Gas { get; set; } = "";
    public bool AutoRun { get; set; } = true;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bridle", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
        }
        catch (Exception)
        {
            // Bozuk ayar dosyası uygulamayı durdurmamalı.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception)
        {
            // Ayarlar kaydedilemezse çalışmaya devam edilir.
        }
    }
}

/// <summary>Bridle deposunun ve komşu Horseman deposunun yerini bulur.</summary>
internal static class Workspace
{
    /// <summary>"versions" klasörünü içeren en yakın üst klasör; yoksa null.</summary>
    public static string? Root { get; } = FindRoot();

    private static string? FindRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "versions")) &&
                Directory.Exists(Path.Combine(d.FullName, "src")))
            {
                return d.FullName;
            }
        }
        return null;
    }

    /// <summary>Bridle\versions altındaki DLL'ler: bırakılan her dosya bir sürümdür.</summary>
    public static IEnumerable<string> VersionDlls()
    {
        if (Root == null) return Array.Empty<string>();
        return Directory.EnumerateFiles(Path.Combine(Root, "versions"), "*.dll").Order();
    }

    public static string? VersionsDir => Root == null ? null : Path.Combine(Root, "versions");

    /// <summary>
    /// Komşu Horseman deposunun üzerinde çalışılan derlemesi; varsa ilk
    /// açılışta kendiliğinden yüklenir ve her yeniden derlemede yenilenir.
    /// </summary>
    public static string? LiveDll()
    {
        if (Root == null) return null;
        string parent = Path.GetDirectoryName(Root) ?? Root;
        string[] candidates =
        {
            Path.Combine(parent, "Horseman", "build", "Debug", "bridle", "horseman_bridle.dll"),
            Path.Combine(parent, "Horseman", "build", "Release", "bridle", "horseman_bridle.dll"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Örnek .hm programları: Bridle\examples ve komşu Horseman deposu.</summary>
    public static List<string> ExampleFiles()
    {
        var files = new List<string>();
        if (Root == null) return files;
        string parent = Path.GetDirectoryName(Root) ?? Root;
        string[] dirs =
        {
            Path.Combine(Root, "examples"),
            Path.Combine(parent, "Horseman", "examples"),
            Path.Combine(parent, "Horseman", "tests", "lang"),
        };
        foreach (string dir in dirs)
        {
            if (Directory.Exists(dir)) files.AddRange(Directory.EnumerateFiles(dir, "*.hm").Order());
        }
        return files;
    }
}
