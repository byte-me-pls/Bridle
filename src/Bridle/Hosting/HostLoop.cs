using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bridle.Native;

namespace Bridle.Hosting;

/// <summary>
/// Host kipi: <c>Bridle.exe --host &lt;dll&gt;</c>.
///
/// Tek bir DLL'i yükler ve stdin'den gelen istekleri yanıtlar. Her sürüm
/// kendi sürecinde çalışır; böylece deneysel bir DLL çökerse ya da sonsuz
/// döngüye girerse yalnızca o süreç ölür, arayüz ayakta kalır.
///
/// Protokol satır tabanlıdır, UTF-8:
///   açılış  → {"abi":1,"describe":{...}}   ya da   {"error":"..."}
///   istek   ← {"stage":"tokens","source":"...","options":"..."}
///   yanıt   → {"elapsedMs":0.42,"result":{...}}   ya da   {"error":"..."}
/// </summary>
internal static class HostLoop
{
    public static int Run(string dllPath)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };

        BridleLibrary library;
        try
        {
            library = BridleLibrary.Load(dllPath);
        }
        catch (Exception ex)
        {
            output.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }));
            return 1;
        }

        using (library)
        {
            int abi = library.AbiVersion;
            if (abi != BridleLibrary.SupportedAbi)
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    error = $"ABI sürümü {abi}; bu Bridle yalnızca {BridleLibrary.SupportedAbi} sürümünü biliyor."
                }));
                return 1;
            }

            string describe = library.Describe();
            if (!IsJson(describe))
            {
                output.WriteLine(JsonSerializer.Serialize(new { error = "bridle_describe geçerli JSON döndürmedi." }));
                return 1;
            }
            output.WriteLine($"{{\"abi\":{abi},\"describe\":{describe}}}");

            string? line;
            while ((line = input.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                output.WriteLine(Handle(library, line));
            }
        }
        return 0;
    }

    private static string Handle(BridleLibrary library, string requestLine)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(requestLine);
            JsonElement root = doc.RootElement;
            string stage = root.GetProperty("stage").GetString() ?? "";
            string source = root.GetProperty("source").GetString() ?? "";
            string options = root.TryGetProperty("options", out JsonElement o) ? o.GetString() ?? "" : "";

            long start = Stopwatch.GetTimestamp();
            string result = library.Run(stage, source, options);
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            // DLL'in yanıtı olduğu gibi gömülür; önce gerçekten JSON olduğu doğrulanır.
            if (!IsJson(result))
            {
                string head = result.Length > 120 ? result[..120] + "…" : result;
                return JsonSerializer.Serialize(new { error = "DLL geçerli, tek satırlık JSON döndürmedi: " + head });
            }
            return $"{{\"elapsedMs\":{elapsedMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},\"result\":{result}}}";
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static bool IsJson(string text)
    {
        // Yanıt tek satır olmalı: protokol satır tabanlı.
        if (text.Contains('\n') || text.Contains('\r')) return false;
        try
        {
            using JsonDocument _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
