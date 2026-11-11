using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Bridle.Hosting;

namespace Bridle;

internal static class Program
{
    /// <summary>
    /// Kipler:
    ///   Bridle.exe                         arayüz
    ///   Bridle.exe --host DLL              tek DLL'i yükleyip stdin'den istek yanıtlar (iç kullanım)
    ///   Bridle.exe --selftest DLL [KAYNAK] arayüzsüz: her aşamayı çalıştırıp sonucu yazar
    ///   Bridle.exe --render PNG [--dll DLL]... [--source DOSYA]
    ///                                      pencereyi ekrana çıkarmadan PNG'ye çizer
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--host") return HostLoop.Run(args[1]);
        if (args.Length >= 2 && args[0] == "--selftest") return SelfTest(args).GetAwaiter().GetResult();
        if (args.Length >= 2 && args[0] == "--render") return Render(args);

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }

    private static async Task<int> SelfTest(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        using var host = new VersionHost(args[1]);
        await host.StartAsync();
        if (!host.IsReady)
        {
            Console.WriteLine("YUKLENEMEDI: " + host.LoadError);
            return 1;
        }

        VersionInfo info = host.Info!;
        Console.WriteLine($"{info.Name} {info.Version} ({info.Build}), {info.Compiler}, {info.Built}");

        string source = args.Length > 2
            ? File.ReadAllText(args[2])
            : "module t; profile core; output i64; fn main() -> i64 { return 6 * 7; }";
        string options = args.Length > 3 ? string.Join('\n', args[3..]) : "";

        int failures = 0;
        foreach (StageInfo stage in info.Stages)
        {
            StageResult r = await host.RunAsync(stage.Id, source, options, TimeSpan.FromSeconds(5));
            Console.WriteLine($"--- {stage.Id} ({stage.Title}): ok={r.Ok}, {r.ElapsedMs:0.###} ms, {r.Summary}");
            Console.Write(r.DisplayText);
            if (r.HostError != null) failures++;
        }
        return failures == 0 ? 0 : 1;
    }

    private static int Render(string[] args)
    {
        string png = args[1];
        var settings = new Settings { AutoRun = false };
        for (int i = 2; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--dll") settings.Dlls.Add(args[i + 1]);
            if (args[i] == "--source") settings.Source = File.ReadAllText(args[i + 1]);
            if (args[i] == "--inputs") settings.Inputs = args[i + 1].Replace(';', '\n');
        }

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var window = new MainWindow(settings, persist: false) { Width = 1500, Height = 900 };
        window.Show();

        // Arka plandaki host çağrıları bitene dek arayüz kuyruğunu elle döndür.
        Task work = window.StartAsync(compareFirstTwo: true);
        while (!work.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        if (work.IsFaulted) Console.WriteLine(work.Exception);

        var frame = window.CaptureRenderedFrame();
        if (frame == null)
        {
            Console.WriteLine("cizim alinamadi");
            return 1;
        }
        frame.Save(png);
        window.Close();
        Console.WriteLine("yazildi: " + png);
        return 0;
    }
}
