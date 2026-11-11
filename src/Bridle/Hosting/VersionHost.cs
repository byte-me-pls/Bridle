using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Bridle.Hosting;

/// <summary>
/// Yüklenmiş bir Horseman derlemesi: DLL'in gölge kopyasını ayrı bir host
/// sürecinde çalıştırır ve onunla konuşur.
///
/// Gölge kopya sayesinde özgün DLL kilitlenmez: Bridle açıkken Horseman
/// yeniden derlenebilir, sonra <see cref="StartAsync"/> ile yenisi yüklenir.
/// </summary>
internal sealed class VersionHost : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private string? _shadowDir;
    private readonly StringBuilder _stderr = new();

    public VersionHost(string dllPath)
    {
        DllPath = Path.GetFullPath(dllPath);
        Label = Path.GetFileNameWithoutExtension(DllPath);
    }

    public string DllPath { get; }

    /// <summary>Listede görünen ad; varsayılan olarak dosya adı.</summary>
    public string Label { get; set; }

    public VersionInfo? Info { get; private set; }

    /// <summary>Yükleme hatası; null ise sürüm kullanılabilir.</summary>
    public string? LoadError { get; private set; }

    public DateTime LoadedFileTimeUtc { get; private set; }

    public bool IsReady => Info != null && LoadError == null;

    /// <summary>DLL'i (yeniden) yükler. Hata fırlatmaz; LoadError'a yazar.</summary>
    public async Task StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopProcess();
            Info = null;
            LoadError = null;
            await SpawnAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            StopProcess();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SpawnAsync()
    {
        if (!File.Exists(DllPath)) throw new FileNotFoundException("DLL bulunamadı: " + DllPath);

        // Gölge kopya: özgün dosya serbest kalır.
        _shadowDir = Path.Combine(Path.GetTempPath(), "bridle", "shadow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_shadowDir);
        string shadow = Path.Combine(_shadowDir, Path.GetFileName(DllPath));
        File.Copy(DllPath, shadow);
        LoadedFileTimeUtc = File.GetLastWriteTimeUtc(DllPath);

        var utf8 = new UTF8Encoding(false);
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Bridle'ın yolu bulunamadı."))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            // DLL'in kendi bağımlılıkları özgün klasöründen çözülebilsin.
            WorkingDirectory = Path.GetDirectoryName(DllPath) ?? Environment.CurrentDirectory,
        };
        info.ArgumentList.Add("--host");
        info.ArgumentList.Add(shadow);

        _stderr.Clear();
        var process = new Process { StartInfo = info };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_stderr)
            {
                if (_stderr.Length < 4000) _stderr.AppendLine(e.Data);
            }
        };
        process.Start();
        process.BeginErrorReadLine();
        _process = process;

        string? hello = await ReadLineAsync(process, StartTimeout).ConfigureAwait(false);
        if (hello == null) throw new InvalidOperationException(DescribeDeath(process, "yüklenirken"));

        using JsonDocument doc = JsonDocument.Parse(hello);
        if (doc.RootElement.TryGetProperty("error", out JsonElement error))
        {
            throw new InvalidOperationException(error.GetString() ?? "bilinmeyen hata");
        }
        Info = VersionInfo.Parse(doc.RootElement.GetProperty("describe"));
    }

    /// <summary>
    /// Aşamayı çalıştırır. Süreç ölürse ya da süre aşılırsa hata sonucu döner
    /// ve süreç bir sonraki çağrıda yeniden başlatılır.
    /// </summary>
    public async Task<StageResult> RunAsync(string stage, string source, string options, TimeSpan timeout)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (LoadError != null) return StageResult.Failure("Yüklenemedi: " + LoadError);

            if (_process == null || _process.HasExited)
            {
                // Önceki çağrıda öldüyse sessizce yeniden başlat.
                try
                {
                    StopProcess();
                    await SpawnAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    StopProcess();
                    return StageResult.Failure("Yeniden başlatılamadı: " + ex.Message);
                }
            }

            Process process = _process!;
            string request = JsonSerializer.Serialize(new { stage, source, options });
            try
            {
                await process.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                return StageResult.Failure(DescribeDeath(process, $"'{stage}' aşamasında"));
            }

            string? line;
            try
            {
                line = await ReadLineAsync(process, timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                StopProcess();
                return StageResult.Failure(
                    $"Zaman aşımı: '{stage}' aşaması {timeout.TotalSeconds:0.#} saniyede bitmedi; süreç sonlandırıldı.");
            }
            if (line == null) return StageResult.Failure(DescribeDeath(process, $"'{stage}' aşamasında"));

            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("error", out JsonElement error))
            {
                return StageResult.Failure(error.GetString() ?? "bilinmeyen hata");
            }
            double elapsed = root.TryGetProperty("elapsedMs", out JsonElement ms) ? ms.GetDouble() : 0;
            return StageResult.Parse(root.GetProperty("result"), elapsed);
        }
        catch (Exception ex)
        {
            return StageResult.Failure("Beklenmeyen hata: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string?> ReadLineAsync(Process process, TimeSpan timeout)
    {
        Task<string?> read = process.StandardOutput.ReadLineAsync();
        Task finished = await Task.WhenAny(read, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != read) throw new TimeoutException();
        return await read.ConfigureAwait(false);
    }

    /// <summary>Sürecin neden öldüğünü okunur biçimde anlatır.</summary>
    private string DescribeDeath(Process process, string when)
    {
        string code = "bilinmiyor";
        try
        {
            if (process.WaitForExit(2000))
            {
                uint raw = unchecked((uint)process.ExitCode);
                code = raw switch
                {
                    0xC0000005 => "0xC0000005, erişim ihlali",
                    0xC00000FD => "0xC00000FD, yığın taşması",
                    0xC0000409 => "0xC0000409, yığın bozulması ya da abort",
                    0xC0000094 => "0xC0000094, sıfıra bölme",
                    _ when raw > 255 => $"0x{raw:X8}",
                    _ => raw.ToString(),
                };
            }
        }
        catch (InvalidOperationException)
        {
        }

        string stderr;
        lock (_stderr) stderr = _stderr.ToString().Trim();
        string tail = stderr.Length > 0 ? "\n" + stderr : "";
        return $"DLL {when} çöktü (çıkış kodu {code}).{tail}";
    }

    private void StopProcess()
    {
        Process? process = _process;
        _process = null;
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch (Exception)
            {
                // Süreç zaten gitmiş olabilir.
            }
            process.Dispose();
        }

        if (_shadowDir != null)
        {
            try
            {
                Directory.Delete(_shadowDir, recursive: true);
            }
            catch (Exception)
            {
                // Kilitliyse temp klasöründe kalır; zararsız.
            }
            _shadowDir = null;
        }
    }

    public void Dispose()
    {
        StopProcess();
        _gate.Dispose();
    }
}
