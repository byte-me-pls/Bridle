using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Bridle.Diff;
using Bridle.Hosting;

namespace Bridle;

/// <summary>
/// Ana pencere. Sol: yüklü sürümler ve A/B seçimi. Orta: kaynak ve girdiler.
/// Sağ: her aşama için bir sekme; B seçiliyse iki sürümün çıktısı yan yana,
/// farklı satırlar boyalı.
/// </summary>
internal sealed class MainWindow : Window
{
    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,Courier New,monospace");
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(5);
    private const string NoCompare = "(karşılaştırma yok)";

    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8a8f98"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#6cc47a"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#f07878"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#e0b050"));
    private static readonly IBrush PanelBg = new SolidColorBrush(Color.Parse("#1e2127"));
    private static readonly IBrush ChangedBg = new SolidColorBrush(Color.Parse("#4a3d10"));
    private static readonly IBrush RemovedBg = new SolidColorBrush(Color.Parse("#4d2222"));
    private static readonly IBrush AddedBg = new SolidColorBrush(Color.Parse("#1f4429"));
    private static readonly IBrush EmptyBg = new SolidColorBrush(Color.Parse("#16181c"));

    private readonly Settings _settings;
    private readonly bool _persist;
    private readonly List<VersionHost> _versions = new();
    private readonly Dictionary<VersionHost, FileSystemWatcher> _watchers = new();

    private readonly TextBox _editor;
    private readonly TextBox _inputs;
    private readonly TextBox _gas;
    private readonly ComboBox _examples;
    private readonly ComboBox _selectA;
    private readonly ComboBox _selectB;
    private readonly CheckBox _autoRun;
    private readonly StackPanel _versionList;
    private readonly TabControl _tabs;
    private readonly ListBox _diagnostics;
    private readonly TextBlock _status;
    private readonly TextBlock _caret;
    private readonly DispatcherTimer _debounce;

    private List<string> _exampleFiles = new();
    private List<(string Who, Diagnostic Diag)> _shownDiagnostics = new();
    private List<StageInfo> _stages = new();
    private Dictionary<string, (StageResult? A, StageResult? B)> _results = new();
    private string? _currentFile;
    private int _runSerial;
    private bool _updatingSelectors;
    private bool _started;

    public MainWindow(Settings settings, bool persist)
    {
        _settings = settings;
        _persist = persist;

        Title = "Bridle — Horseman lab";
        Width = 1500;
        Height = 900;
        MinWidth = 900;
        MinHeight = 500;

        _editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Mono,
            FontSize = 13,
            Text = settings.Source,
            Watermark = "HSL kaynağını buraya yazın ya da üstten bir örnek seçin",
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);

        _inputs = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Mono,
            FontSize = 13,
            Text = settings.Inputs,
            Watermark = "her satıra bir girdi:  ad=1,2,3",
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        _gas = new TextBox { FontFamily = Mono, FontSize = 13, Width = 130, Text = settings.Gas, Watermark = "varsayılan" };

        _examples = new ComboBox { PlaceholderText = "Örnekler", MinWidth = 220, MaxDropDownHeight = 500 };
        _selectA = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _selectB = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _autoRun = new CheckBox { Content = "Yazarken çalıştır", IsChecked = settings.AutoRun };
        _versionList = new StackPanel { Spacing = 6 };
        _tabs = new TabControl();
        _diagnostics = new ListBox { FontFamily = Mono, FontSize = 12, Height = 120, Background = PanelBg, CornerRadius = new CornerRadius(4) };
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _caret = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Dim, FontFamily = Mono, FontSize = 12 };

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunAsync();
        };

        Content = BuildLayout();
        WireEvents();
        RefreshVersionList();
        UpdateCaret();

        if (persist) Opened += (_, _) => _ = StartAsync(compareFirstTwo: false);
        Closing += (_, _) => Shutdown();
    }

    // ---- yerleşim ----

    private Control BuildLayout()
    {
        var open = new Button { Content = "Aç…" };
        open.Click += async (_, _) => await OpenFileAsync();
        var save = new Button { Content = "Kaydet" };
        save.Click += async (_, _) => await SaveFileAsync();
        var run = new Button { Content = "Çalıştır  (F5)", FontWeight = FontWeight.SemiBold };
        run.Click += (_, _) => _ = RunAsync();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 8),
            Children = { open, save, _examples, run, _autoRun },
        };

        var statusBar = new DockPanel { Margin = new Thickness(10, 4), LastChildFill = true };
        DockPanel.SetDock(_caret, Dock.Right);
        statusBar.Children.Add(_caret);
        statusBar.Children.Add(_status);

        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("270,4,2*,4,3*") };
        AddColumn(main, BuildVersionPanel(), 0);
        AddColumn(main, Splitter(), 1);
        AddColumn(main, BuildSourcePanel(), 2);
        AddColumn(main, Splitter(), 3);
        AddColumn(main, BuildResultPanel(), 4);

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(statusBar);
        root.Children.Add(main);
        return root;
    }

    private static void AddColumn(Grid grid, Control control, int column)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    private static GridSplitter Splitter() =>
        new() { ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent };

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontWeight = FontWeight.SemiBold, Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };

    private Control BuildVersionPanel()
    {
        var add = new Button { Content = "DLL ekle…", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        add.Click += async (_, _) => await AddDllAsync();
        var reload = new Button { Content = "Hepsini yenile", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        reload.Click += async (_, _) => await ReloadAllAsync();

        var selectors = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Heading("A — GÖSTERİLEN SÜRÜM"), _selectA,
                new Border { Height = 6 },
                Heading("B — KARŞILAŞTIRILAN SÜRÜM"), _selectB,
            },
        };

        var buttons = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0), Children = { add, reload } };

        var panel = new DockPanel { Margin = new Thickness(10, 0, 6, 0) };
        var top = new StackPanel { Children = { selectors, new Border { Height = 14 }, Heading("YÜKLÜ SÜRÜMLER") } };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(top);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { Content = _versionList });
        return panel;
    }

    private Control BuildSourcePanel()
    {
        var gasRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                new TextBlock { Text = "Gas sınırı", VerticalAlignment = VerticalAlignment.Center, Foreground = Dim, FontSize = 12 },
                _gas,
            },
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,110,Auto"), Margin = new Thickness(2, 0) };
        AddRow(grid, Heading("KAYNAK"), 0);
        AddRow(grid, _editor, 1);
        AddRow(grid, new StackPanel { Margin = new Thickness(0, 8, 0, 0), Children = { Heading("GİRDİLER") } }, 2);
        AddRow(grid, _inputs, 3);
        AddRow(grid, gasRow, 4);
        return grid;
    }

    private Control BuildResultPanel()
    {
        var bottom = new StackPanel { Margin = new Thickness(0, 6, 0, 0), Children = { Heading("TANILAR  (tıklayınca kaynakta o satıra gider)"), _diagnostics } };
        var panel = new DockPanel { Margin = new Thickness(6, 0, 10, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        panel.Children.Add(bottom);
        panel.Children.Add(_tabs);
        return panel;
    }

    private static void AddRow(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    private void WireEvents()
    {
        _editor.TextChanged += (_, _) => OnInputChanged();
        _inputs.TextChanged += (_, _) => OnInputChanged();
        _gas.TextChanged += (_, _) => OnInputChanged();
        _editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty) UpdateCaret();
        };

        _selectA.SelectionChanged += (_, _) => OnSelectionChanged();
        _selectB.SelectionChanged += (_, _) => OnSelectionChanged();
        _tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, _tabs)) ShowDiagnosticsForCurrentTab();
        };
        _examples.SelectionChanged += (_, _) => LoadSelectedExample();
        _diagnostics.SelectionChanged += (_, _) => JumpToSelectedDiagnostic();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                _ = RunAsync();
            }
        };
    }

    // ---- başlangıç ve kapanış ----

    /// <summary>Kayıtlı ve "versions" klasöründeki DLL'leri yükler, ilk koşuyu yapar.</summary>
    public async Task StartAsync(bool compareFirstTwo)
    {
        if (_started) return;
        _started = true;

        _exampleFiles = Workspace.ExampleFiles();
        _examples.ItemsSource = _exampleFiles.Select(ExampleLabel).ToList();

        // Sıra: üzerinde çalışılan derleme, kayıtlı DLL'ler, versions klasörü.
        var paths = new List<string>();
        string? live = _persist ? Workspace.LiveDll() : null;
        if (live != null) paths.Add(live);
        foreach (string dll in _settings.Dlls.Concat(Workspace.VersionDlls()))
        {
            if (!paths.Contains(dll, StringComparer.OrdinalIgnoreCase)) paths.Add(dll);
        }

        SetStatus($"{paths.Count} DLL yükleniyor…", Dim);
        foreach (string path in paths)
        {
            var host = new VersionHost(path);
            if (string.Equals(path, live, StringComparison.OrdinalIgnoreCase)) host.Label = "canlı (Horseman\\build)";
            _versions.Add(host);
        }
        await Task.WhenAll(_versions.Select(v => v.StartAsync()));
        foreach (VersionHost v in _versions) Watch(v);

        RefreshVersionList();
        RefreshSelectors(_settings.SelectedA, _settings.SelectedB);
        if (compareFirstTwo && _versions.Count >= 2)
        {
            _updatingSelectors = true;
            _selectA.SelectedIndex = 0;
            _selectB.SelectedIndex = 2; // 0: karşılaştırma yok, 1: ilk sürüm
            _updatingSelectors = false;
        }

        if (string.IsNullOrWhiteSpace(_editor.Text) && _exampleFiles.Count > 0)
        {
            _examples.SelectedIndex = 0; // ilk örnek; değişim olayı kaynağı yükler ve çalıştırır
        }
        await RunAsync();
    }

    private void Shutdown()
    {
        _debounce.Stop();
        if (_persist)
        {
            _settings.Dlls = _versions.Select(v => v.DllPath).ToList();
            _settings.SelectedA = SelectedA?.DllPath;
            _settings.SelectedB = SelectedB?.DllPath;
            _settings.Source = _editor.Text ?? "";
            _settings.Inputs = _inputs.Text ?? "";
            _settings.Gas = _gas.Text ?? "";
            _settings.AutoRun = _autoRun.IsChecked == true;
            _settings.LastFile = _currentFile;
            _settings.Save();
        }
        foreach (FileSystemWatcher w in _watchers.Values) w.Dispose();
        _watchers.Clear();
        foreach (VersionHost v in _versions) v.Dispose();
        _versions.Clear();
    }

    // ---- sürümler ----

    private VersionHost? SelectedA =>
        _selectA.SelectedIndex >= 0 && _selectA.SelectedIndex < _versions.Count ? _versions[_selectA.SelectedIndex] : null;

    private VersionHost? SelectedB =>
        _selectB.SelectedIndex >= 1 && _selectB.SelectedIndex <= _versions.Count ? _versions[_selectB.SelectedIndex - 1] : null;

    private static string Describe(VersionHost v)
    {
        if (v.Info is { } info) return $"{info.Name} {info.Version} · {info.Build}";
        return v.LoadError != null ? "yüklenemedi" : "yükleniyor…";
    }

    private void RefreshVersionList()
    {
        _versionList.Children.Clear();
        if (_versions.Count == 0)
        {
            _versionList.Children.Add(new TextBlock
            {
                Text = "Henüz DLL yok.\n\n\"DLL ekle…\" ile Horseman'ın derlediği horseman_bridle.dll dosyasını seçin, " +
                       "ya da karşılaştırmak istediğiniz derlemeleri Bridle\\versions klasörüne kopyalayın.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Dim,
                FontSize = 12,
            });
            return;
        }

        foreach (VersionHost v in _versions)
        {
            VersionHost host = v;
            var title = new TextBlock { Text = host.Label, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = new TextBlock
            {
                Text = host.Info is { } info
                    ? $"{info.Build}\n{info.Compiler} · {info.Built}"
                    : host.LoadError ?? "yükleniyor…",
                Foreground = host.LoadError != null ? Bad : Dim,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            };

            var reload = new Button { Content = "↻", Padding = new Thickness(7, 2), FontSize = 12 };
            ToolTip.SetTip(reload, "DLL'i diskten yeniden yükle");
            reload.Click += async (_, _) => await ReloadAsync(host);
            var remove = new Button { Content = "✕", Padding = new Thickness(7, 2), FontSize = 12 };
            ToolTip.SetTip(remove, "Listeden kaldır (dosya silinmez)");
            remove.Click += (_, _) => Remove(host);

            var snapshot = new Button { Content = "⎘", Padding = new Thickness(7, 2), FontSize = 12, IsEnabled = host.IsReady && Workspace.VersionsDir != null };
            ToolTip.SetTip(snapshot, "Anlık kopya al: bu derlemeyi versions klasörüne kaydedip ayrı bir sürüm olarak yükler. " +
                                     "Kodu değiştirmeden önce alın, sonra yenisiyle karşılaştırın.");
            snapshot.Click += async (_, _) => await SnapshotAsync(host);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top, Children = { snapshot, reload, remove } };
            var text = new StackPanel { Spacing = 2, Children = { title, detail } };

            var row = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(text);

            var card = new Border { Background = PanelBg, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), Child = row };
            ToolTip.SetTip(card, host.DllPath);
            _versionList.Children.Add(card);
        }
    }

    /// <summary>A/B listelerini yeniden kurar; mümkünse aynı DLL'ler seçili kalır.</summary>
    private void RefreshSelectors(string? keepA, string? keepB)
    {
        _updatingSelectors = true;
        try
        {
            var labels = _versions.Select(v => $"{v.Label}  —  {Describe(v)}").ToList();
            _selectA.ItemsSource = labels;
            _selectB.ItemsSource = new[] { NoCompare }.Concat(labels).ToList();

            int a = _versions.FindIndex(v => string.Equals(v.DllPath, keepA, StringComparison.OrdinalIgnoreCase));
            int b = _versions.FindIndex(v => string.Equals(v.DllPath, keepB, StringComparison.OrdinalIgnoreCase));
            _selectA.SelectedIndex = a >= 0 ? a : (_versions.Count > 0 ? 0 : -1);
            _selectB.SelectedIndex = b >= 0 ? b + 1 : 0;
        }
        finally
        {
            _updatingSelectors = false;
        }
    }

    private void OnSelectionChanged()
    {
        if (_updatingSelectors) return;
        _ = RunAsync();
    }

    private async Task AddDllAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Horseman DLL'i seç",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Bridle DLL") { Patterns = new[] { "*.dll", "*.so" } } },
        });

        string? keepA = SelectedA?.DllPath, keepB = SelectedB?.DllPath;
        foreach (IStorageFile file in files)
        {
            string? path = file.TryGetLocalPath();
            if (path == null) continue;
            if (_versions.Any(v => string.Equals(v.DllPath, path, StringComparison.OrdinalIgnoreCase))) continue;

            var host = new VersionHost(path);
            _versions.Add(host);
            await host.StartAsync();
            Watch(host);
        }
        RefreshVersionList();
        RefreshSelectors(keepA, keepB);
        await RunAsync();
    }

    /// <summary>
    /// Derlemenin o anki halini versions klasörüne kopyalar ve ayrı bir sürüm
    /// olarak yükler; yeni kopya B'ye yerleşir ki hemen karşılaştırılabilsin.
    /// </summary>
    private async Task SnapshotAsync(VersionHost host)
    {
        string? dir = Workspace.VersionsDir;
        if (dir == null || host.Info == null) return;
        try
        {
            string build = string.Concat(host.Info.Build.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
            string target = Path.Combine(dir, $"{build}_{DateTime.Now:yyyyMMdd-HHmmss}.dll");
            File.Copy(host.DllPath, target);

            var copy = new VersionHost(target);
            _versions.Add(copy);
            await copy.StartAsync();
            Watch(copy);

            RefreshVersionList();
            RefreshSelectors(SelectedA?.DllPath ?? host.DllPath, target);
            await RunAsync();
            SetStatus("Anlık kopya alındı: " + Path.GetFileName(target), Good);
        }
        catch (Exception ex)
        {
            SetStatus("Anlık kopya alınamadı: " + ex.Message, Bad);
        }
    }

    private async Task ReloadAsync(VersionHost host)
    {
        string? keepA = SelectedA?.DllPath, keepB = SelectedB?.DllPath;
        await host.StartAsync();
        RefreshVersionList();
        RefreshSelectors(keepA, keepB);
        await RunAsync();
    }

    private async Task ReloadAllAsync()
    {
        string? keepA = SelectedA?.DllPath, keepB = SelectedB?.DllPath;
        await Task.WhenAll(_versions.Select(v => v.StartAsync()));
        RefreshVersionList();
        RefreshSelectors(keepA, keepB);
        await RunAsync();
    }

    private void Remove(VersionHost host)
    {
        string? keepA = SelectedA?.DllPath, keepB = SelectedB?.DllPath;
        if (_watchers.Remove(host, out FileSystemWatcher? watcher)) watcher.Dispose();
        _versions.Remove(host);
        host.Dispose();
        RefreshVersionList();
        RefreshSelectors(keepA, keepB);
        _ = RunAsync();
    }

    /// <summary>DLL diskte değişince (Horseman yeniden derlenince) kendiliğinden yenile.</summary>
    private void Watch(VersionHost host)
    {
        try
        {
            string? dir = Path.GetDirectoryName(host.DllPath);
            if (dir == null || !Directory.Exists(dir)) return;

            var watcher = new FileSystemWatcher(dir, Path.GetFileName(host.DllPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            CancellationTokenSource? pending = null;
            void Changed(object? _, FileSystemEventArgs __)
            {
                // Bağlayıcı dosyayı birkaç adımda yazar; son olaydan sonra biraz bekle.
                pending?.Cancel();
                var cts = pending = new CancellationTokenSource();
                Task.Delay(700, cts.Token).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_versions.Contains(host)) _ = ReloadAsync(host);
                    });
                }, TaskScheduler.Default);
            }
            watcher.Changed += Changed;
            watcher.Created += Changed;
            watcher.Renamed += (s, e) => Changed(s, e);
            _watchers[host] = watcher;
        }
        catch (Exception)
        {
            // İzleme kurulamazsa elle yenileme hâlâ çalışır.
        }
    }

    // ---- dosya ve örnekler ----

    private static string ExampleLabel(string path)
    {
        string dir = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        return $"{dir}/{Path.GetFileName(path)}";
    }

    private void LoadSelectedExample()
    {
        int index = _examples.SelectedIndex;
        if (index < 0 || index >= _exampleFiles.Count) return;
        LoadFile(_exampleFiles[index]);
    }

    private void LoadFile(string path)
    {
        try
        {
            string text = File.ReadAllText(path);
            _currentFile = path;
            _editor.Text = text;
            _inputs.Text = InputsFromDirectives(text);
            Title = $"Bridle — {Path.GetFileName(path)}";
            _debounce.Stop();
            _ = RunAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Dosya okunamadı: " + ex.Message, Bad);
        }
    }

    /// <summary>
    /// Horseman golden testlerindeki "// input ad: 1, 2, 3" yönergelerini
    /// girdi kutusuna taşır; örnek açılınca doğrudan çalışsın.
    /// </summary>
    private static string InputsFromDirectives(string source)
    {
        var lines = new List<string>();
        foreach (string raw in source.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith("//", StringComparison.Ordinal)) break;
            string body = line[2..].Trim();
            if (!body.StartsWith("input ", StringComparison.Ordinal)) continue;
            int colon = body.IndexOf(':');
            if (colon < 0) continue;
            string name = body[6..colon].Trim();
            string values = body[(colon + 1)..].Replace(" ", "");
            lines.Add($"{name}={values}");
        }
        return string.Join('\n', lines);
    }

    private async Task OpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "HSL kaynağı aç",
            FileTypeFilter = new[] { new FilePickerFileType("Horseman kaynağı") { Patterns = new[] { "*.hm" } }, FilePickerFileTypes.All },
        });
        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path != null) LoadFile(path);
    }

    private async Task SaveFileAsync()
    {
        try
        {
            if (_currentFile == null)
            {
                IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "HSL kaynağını kaydet",
                    DefaultExtension = "hm",
                    FileTypeChoices = new[] { new FilePickerFileType("Horseman kaynağı") { Patterns = new[] { "*.hm" } } },
                });
                _currentFile = file?.TryGetLocalPath();
                if (_currentFile == null) return;
            }
            // BOM'suz UTF-8: Horseman lexer'ı BOM'u reddeder.
            File.WriteAllText(_currentFile, _editor.Text ?? "", new System.Text.UTF8Encoding(false));
            Title = $"Bridle — {Path.GetFileName(_currentFile)}";
            SetStatus("Kaydedildi: " + _currentFile, Good);
        }
        catch (Exception ex)
        {
            SetStatus("Kaydedilemedi: " + ex.Message, Bad);
        }
    }

    // ---- çalıştırma ----

    private void OnInputChanged()
    {
        if (!_started || _autoRun.IsChecked != true) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private string BuildOptions()
    {
        var lines = new List<string>();
        foreach (string raw in (_inputs.Text ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0) lines.Add("input:" + line);
        }
        string gas = (_gas.Text ?? "").Trim().Replace("_", "");
        if (gas.Length > 0) lines.Add("gas:" + gas);
        return string.Join('\n', lines);
    }

    /// <summary>Kaynağı seçili sürümlerde bütün aşamalardan geçirir ve sekmeleri günceller.</summary>
    public async Task RunAsync()
    {
        int serial = ++_runSerial;
        VersionHost? a = SelectedA, b = SelectedB;
        if (a == null)
        {
            _stages = new List<StageInfo>();
            _results = new();
            RebuildTabs(null, null);
            SetStatus(_versions.Count == 0 ? "DLL yüklü değil." : "A sürümü seçili değil.", Dim);
            return;
        }

        // Sekmeler: A'nın aşamaları, ardından yalnızca B'de olanlar.
        var stages = new List<StageInfo>(a.Info?.Stages ?? Array.Empty<StageInfo>());
        foreach (StageInfo s in b?.Info?.Stages ?? Array.Empty<StageInfo>())
        {
            if (!stages.Any(x => x.Id == s.Id)) stages.Add(s);
        }

        string source = _editor.Text ?? "";
        string options = BuildOptions();
        SetStatus("Çalışıyor…", Dim);

        Task<StageResult[]> runA = RunStagesAsync(a, stages, source, options);
        Task<StageResult[]>? runB = b != null ? RunStagesAsync(b, stages, source, options) : null;
        StageResult[] resultsA = await runA;
        StageResult[]? resultsB = runB != null ? await runB : null;

        if (serial != _runSerial) return; // bu arada daha yeni bir koşu başladı

        var results = new Dictionary<string, (StageResult?, StageResult?)>();
        for (int i = 0; i < stages.Count; i++)
        {
            results[stages[i].Id] = (resultsA[i], resultsB?[i]);
        }
        _stages = stages;
        _results = results;
        RebuildTabs(a, b);
        UpdateStatus(a, b);
    }

    private static async Task<StageResult[]> RunStagesAsync(VersionHost host, List<StageInfo> stages, string source, string options)
    {
        var results = new StageResult[stages.Count];
        for (int i = 0; i < stages.Count; i++)
        {
            bool supported = host.Info?.Stages.Any(s => s.Id == stages[i].Id) ?? false;
            results[i] = host.IsReady && !supported
                ? StageResult.Failure("Bu sürümde bu aşama yok.")
                : await host.RunAsync(stages[i].Id, source, options, StageTimeout);
        }
        return results;
    }

    // ---- sonuçların gösterimi ----

    private void RebuildTabs(VersionHost? a, VersionHost? b)
    {
        string? selected = (_tabs.SelectedItem as TabItem)?.Tag as string;
        _tabs.Items.Clear();

        foreach (StageInfo stage in _stages)
        {
            (StageResult? ra, StageResult? rb) = _results[stage.Id];
            if (ra == null) continue;

            Control content;
            string marker = "";
            IBrush markerBrush = Dim;

            if (rb == null)
            {
                content = SingleView(ra);
                if (!ra.Ok)
                {
                    marker = "  ●";
                    markerBrush = Bad;
                }
            }
            else
            {
                List<DiffRow> rows = LineDiff.Compute(ra.DisplayText, rb.DisplayText);
                int differences = LineDiff.CountDifferences(rows);
                content = CompareView(a!, b!, ra, rb, rows, differences);
                if (differences > 0)
                {
                    marker = "  ≠";
                    markerBrush = Warn;
                }
                else if (!ra.Ok)
                {
                    marker = "  ●";
                    markerBrush = Bad;
                }
            }

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = stage.Title, FontSize = 14 },
                    new TextBlock { Text = marker, Foreground = markerBrush, FontSize = 14, FontWeight = FontWeight.Bold },
                },
            };
            _tabs.Items.Add(new TabItem { Header = header, Content = content, Tag = stage.Id });
        }

        int index = 0;
        for (int i = 0; i < _tabs.Items.Count; i++)
        {
            if ((_tabs.Items[i] as TabItem)?.Tag as string == selected) index = i;
        }
        if (_tabs.Items.Count > 0) _tabs.SelectedIndex = index;
        ShowDiagnosticsForCurrentTab();
    }

    private static TextBlock ResultCaption(string who, StageResult r)
    {
        string state = r.HostError != null ? "süreç hatası" : r.Ok ? "tamam" : "başarısız";
        string summary = r.Summary.Length > 0 ? "  ·  " + r.Summary : "";
        string time = r.HostError == null ? $"  ·  {r.ElapsedMs:0.###} ms" : "";
        return new TextBlock
        {
            Text = $"{who}  ·  {state}{summary}{time}",
            Foreground = r.HostError != null || !r.Ok ? Bad : Good,
            FontSize = 12,
            Margin = new Thickness(2, 0, 2, 6),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
    }

    private static Control SingleView(StageResult r)
    {
        var text = new TextBox
        {
            Text = r.DisplayText,
            IsReadOnly = true,
            FontFamily = Mono,
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);

        var panel = new DockPanel();
        TextBlock caption = ResultCaption("A", r);
        DockPanel.SetDock(caption, Dock.Top);
        panel.Children.Add(caption);
        panel.Children.Add(text);
        return panel;
    }

    private static Control CompareView(VersionHost a, VersionHost b, StageResult ra, StageResult rb, List<DiffRow> rows, int differences)
    {
        var verdict = new TextBlock
        {
            Text = differences == 0 ? "İki sürümün çıktısı aynı." : $"{differences} satır farklı.",
            Foreground = differences == 0 ? Good : Warn,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(2, 0, 2, 6),
        };

        var captions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        TextBlock left = ResultCaption("A  " + a.Label, ra);
        TextBlock right = ResultCaption("B  " + b.Label, rb);
        Grid.SetColumn(right, 1);
        captions.Children.Add(left);
        captions.Children.Add(right);

        var list = new ListBox
        {
            ItemsSource = rows,
            ItemTemplate = new FuncDataTemplate<DiffRow>((row, _) => DiffRowView(row), supportsRecycling: false),
            Padding = new Thickness(0),
        };
        // Satırlar bitişik dursun: ListBoxItem'ın varsayılan iç boşluğu kaldırılır.
        list.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 0.0),
            },
        });

        var header = new StackPanel { Children = { verdict, captions } };
        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(list);
        return panel;
    }

    private static Control DiffRowView(DiffRow? row)
    {
        if (row == null) return new Border();

        (IBrush leftBg, IBrush rightBg) = row.Kind switch
        {
            DiffKind.Changed => (ChangedBg, ChangedBg),
            DiffKind.LeftOnly => (RemovedBg, EmptyBg),
            DiffKind.RightOnly => (EmptyBg, AddedBg),
            _ => (Brushes.Transparent, Brushes.Transparent),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,2,*") };
        Control leftCell = DiffCell(row.LeftNo, row.Left, leftBg);
        Control rightCell = DiffCell(row.RightNo, row.Right, rightBg);
        Grid.SetColumn(rightCell, 2);
        grid.Children.Add(leftCell);
        grid.Children.Add(rightCell);
        return grid;
    }

    private static Control DiffCell(int? number, string text, IBrush background)
    {
        var no = new TextBlock
        {
            Text = number?.ToString() ?? "",
            Width = 44,
            TextAlignment = TextAlignment.Right,
            Foreground = Dim,
            FontFamily = Mono,
            FontSize = 12,
            Margin = new Thickness(0, 0, 10, 0),
        };
        var body = new TextBlock { Text = text, FontFamily = Mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(body, text);

        var row = new DockPanel();
        DockPanel.SetDock(no, Dock.Left);
        row.Children.Add(no);
        row.Children.Add(body);
        return new Border { Background = background, Padding = new Thickness(0, 1), Child = row };
    }

    private void ShowDiagnosticsForCurrentTab()
    {
        _shownDiagnostics = new();
        if ((_tabs.SelectedItem as TabItem)?.Tag is string id && _results.TryGetValue(id, out var pair))
        {
            if (pair.A != null) _shownDiagnostics.AddRange(pair.A.Diagnostics.Select(d => ("A", d)));
            if (pair.B != null) _shownDiagnostics.AddRange(pair.B.Diagnostics.Select(d => ("B", d)));
        }
        _diagnostics.ItemsSource = _shownDiagnostics.Select(x => $"{x.Who}  {x.Diag}").ToList();
    }

    private void JumpToSelectedDiagnostic()
    {
        int index = _diagnostics.SelectedIndex;
        if (index < 0 || index >= _shownDiagnostics.Count) return;
        Diagnostic d = _shownDiagnostics[index].Diag;
        if (d.Line <= 0) return;

        string text = _editor.Text ?? "";
        int offset = 0;
        for (int line = 1; line < d.Line; line++)
        {
            int next = text.IndexOf('\n', offset);
            if (next < 0) return;
            offset = next + 1;
        }
        // Sütun bayt cinsindendir; kaynak ASCII olduğu sürece karakterle aynıdır.
        offset = Math.Min(text.Length, offset + Math.Max(0, d.Column - 1));
        _editor.CaretIndex = offset;
        _editor.SelectionStart = offset;
        _editor.SelectionEnd = Math.Min(text.Length, offset + 1);
        _editor.Focus();
    }

    // ---- durum çubuğu ----

    private void SetStatus(string text, IBrush brush)
    {
        _status.Text = text;
        _status.Foreground = brush;
    }

    private void UpdateStatus(VersionHost a, VersionHost? b)
    {
        if (b == null)
        {
            var failed = _stages.Where(s => _results[s.Id].A is { Ok: false }).Select(s => s.Title).ToList();
            double total = _stages.Sum(s => _results[s.Id].A?.ElapsedMs ?? 0);
            SetStatus(
                failed.Count == 0
                    ? $"{a.Label}: bütün aşamalar tamam  ·  toplam {total:0.##} ms"
                    : $"{a.Label}: başarısız aşamalar: {string.Join(", ", failed)}",
                failed.Count == 0 ? Good : Bad);
            return;
        }

        var different = _stages
            .Where(s => _results[s.Id] is { A: { } ra, B: { } rb } && ra.DisplayText != rb.DisplayText)
            .Select(s => s.Title)
            .ToList();
        SetStatus(
            different.Count == 0
                ? $"{a.Label} ile {b.Label}: bütün aşamalarda aynı çıktı"
                : $"{a.Label} ile {b.Label} farklı: {string.Join(", ", different)}",
            different.Count == 0 ? Good : Warn);
    }

    private void UpdateCaret()
    {
        string text = _editor.Text ?? "";
        int caret = Math.Clamp(_editor.CaretIndex, 0, text.Length);
        int line = 1, lineStart = 0;
        for (int i = 0; i < caret; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
        _caret.Text = $"Satır {line}, Sütun {caret - lineStart + 1}";
    }
}
