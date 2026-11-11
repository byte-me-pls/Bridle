using System.Text;
using System.Text.Json;

namespace Bridle.Hosting;

/// <summary>Bir derlemenin çalıştırabildiği aşama (sekme).</summary>
internal sealed record StageInfo(string Id, string Title);

/// <summary>bridle_describe yanıtı.</summary>
internal sealed record VersionInfo(
    string Name, string Version, string Build, string Built, string Compiler,
    IReadOnlyList<StageInfo> Stages)
{
    public static VersionInfo Parse(JsonElement describe)
    {
        var stages = new List<StageInfo>();
        if (describe.TryGetProperty("stages", out JsonElement list) &&
            list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement s in list.EnumerateArray())
            {
                string id = Text(s, "id");
                if (id.Length > 0) stages.Add(new StageInfo(id, Text(s, "title", id)));
            }
        }
        return new VersionInfo(
            Text(describe, "name", "?"), Text(describe, "version"), Text(describe, "build"),
            Text(describe, "built"), Text(describe, "compiler"), stages);
    }

    private static string Text(JsonElement e, string name, string fallback = "") =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;
}

internal sealed record Diagnostic(string Code, int Line, int Column, string Message)
{
    public override string ToString() =>
        Line > 0 ? $"{Line}:{Column}  {Code}  {Message}" : $"{Code}  {Message}";
}

/// <summary>Bir sürümün bir aşama için verdiği sonuç.</summary>
internal sealed record StageResult(
    bool Ok, string Text, string Summary, IReadOnlyList<Diagnostic> Diagnostics,
    double ElapsedMs, string? HostError)
{
    /// <summary>Süreç düzeyinde hata: çökme, zaman aşımı, yüklenemeyen DLL.</summary>
    public static StageResult Failure(string message) =>
        new(false, "", "", Array.Empty<Diagnostic>(), 0, message);

    public static StageResult Parse(JsonElement result, double elapsedMs)
    {
        var diagnostics = new List<Diagnostic>();
        if (result.TryGetProperty("diagnostics", out JsonElement list) &&
            list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement d in list.EnumerateArray())
            {
                diagnostics.Add(new Diagnostic(
                    Str(d, "code"), Int(d, "line"), Int(d, "column"), Str(d, "message")));
            }
        }
        return new StageResult(
            result.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True,
            Str(result, "text"), Str(result, "summary"), diagnostics, elapsedMs, null);
    }

    /// <summary>
    /// Ekranda gösterilen ve sürümler arasında karşılaştırılan metin: aşamanın
    /// çıktısı, ardından tanılar. Süre ve özet karşılaştırmaya girmez.
    /// </summary>
    public string DisplayText
    {
        get
        {
            if (HostError != null) return "!! " + HostError + "\n";
            var sb = new StringBuilder(Text);
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            foreach (Diagnostic d in Diagnostics) sb.Append(d).Append('\n');
            return sb.ToString();
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int n) ? n : 0;
}
