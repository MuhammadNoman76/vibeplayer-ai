using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VibePlayer.Core;
using VibePlayer.Core.Ai;

namespace VibePlayer.Views;

/// <summary>
/// The AI scene filter in the player: scans the open video in the background (from where you're watching
/// first), marks what it found on the progress bar in pink and skips those scenes. Settings › AI scene filter.
/// The file itself is never changed; results are cached per file.
/// </summary>
public partial class PlayerView
{
    private static readonly Color AiMarkColor = Color.FromRgb(0xFF, 0x3D, 0x8B);

    private SceneScan? _aiScan;
    private SceneScanner? _aiScanner;
    private Task _aiStopping = Task.CompletedTask;
    private int _aiGeneration;
    private List<AiScene> _aiScenes = new();
    private List<SkipRange> _aiSkips = new();
    private int _aiSeenVersion = -1;
    private int _aiTick;
    private string? _aiLastStatus;
    private TextBlock? _aiMenuStatus;

    private SceneKind AiKinds =>
        (_settings.AiSkipSexual ? SceneKind.Sexual : SceneKind.None) | (_settings.AiSkipKissing ? SceneKind.Kissing : SceneKind.None);

    private static bool AiCanScan(MediaItem? item) =>
        item is { IsUrl: false } && MediaFormats.KindOf(item.Source) is MediaKind.Video or MediaKind.Unknown;

    /// <summary>A file was opened: load what is already known about it (the scan starts once playback does).</summary>
    private void AiLoad(MediaItem item)
    {
        AiStop();
        _aiScan = AiCanScan(item) ? SceneScan.Load(item.Source, AiEngine.ModelSetFor(_settings.AiNudeNet)) : null;
        _aiLastStatus = null;
        AiRefreshScenes();
    }

    /// <summary>Starts the background scan if the file isn't fully scanned yet and scanning is on.</summary>
    private void AiStartIfNeeded(long focusMs)
    {
        if (_settings.AiAutoScan && AiEngine.IsSupported && _aiScan is { IsComplete: false } && _aiScanner == null)
            AiStartScan(focusMs, fresh: false);
    }

    private async void AiStartScan(long focusMs, bool fresh)
    {
        if (!AiCanScan(_current) || _current == null) return;
        if (!AiEngine.IsSupported)
        {
            ShowToast(AiEngine.UnsupportedReason ?? "The AI scene filter isn't available on this PC.", 5);
            return;
        }

        AiStop();
        var item = _current;
        var modelSet = AiEngine.ModelSetFor(_settings.AiNudeNet);
        if (fresh || _aiScan == null || _aiScan.Source != item.Source || _aiScan.ModelSet != modelSet)
            _aiScan = _aiScan != null && _aiScan.Source == item.Source ? _aiScan.Restart(modelSet) : SceneScan.CreateNew(item.Source, modelSet);
        var scan = _aiScan;
        var generation = ++_aiGeneration;
        _aiLastStatus = "Starting…";
        AiRefreshScenes();
        UpdateAiStatus();

        // The previous scan has to let go of the models first (they may be reloaded for other settings).
        await _aiStopping;
        if (generation != _aiGeneration || !ReferenceEquals(_current, item)) return;

        var scanner = new SceneScanner(scan, item.Source, _settings.AiProcessor, _settings.AiNudeNet, focusMs);
        _aiScanner = scanner;
        scanner.Completed += () => Dispatcher.BeginInvoke(() => OnAiScanCompleted(scanner));
        scanner.Start();
        UpdateAiStatus();
    }

    /// <summary>Stops the scan (it saves what it has in the background).</summary>
    private void AiStop()
    {
        var scanner = _aiScanner;
        _aiScanner = null;
        _aiGeneration++;
        if (scanner == null) return;
        scanner.Cancel();
        _aiStopping = Task.Run(() => scanner.Wait(TimeSpan.FromSeconds(30)));
        _aiLastStatus = scanner.Finished ? "Done" : "Stopped";
        UpdateAiStatus();
    }

    private void OnAiScanCompleted(SceneScanner scanner)
    {
        if (!ReferenceEquals(scanner, _aiScanner)) return;
        _aiScanner = null;
        _aiLastStatus = scanner.Status;
        AiRefreshScenes();
        if (scanner.Failed) ShowToast(scanner.Status, 7);
        else if (scanner.Finished)
        {
            var n = _aiSkips.Count;
            ShowToast(n == 0
                ? "AI scan finished: nothing to skip in this video"
                : $"AI scan finished: {n} scene{(n == 1 ? "" : "s")} will be skipped (pink on the progress bar)", 4.5);
        }
        UpdateAiStatus();
    }

    /// <summary>Once a second from the UI tick: tell the scan where we are and pick up new results.</summary>
    private void AiTick()
    {
        if (++_aiTick % 5 != 0) return;
        _aiScanner?.Focus(_seekTargetMs >= 0 ? _seekTargetMs : _lastTime);
        if (_aiScan != null && _aiScan.Version != _aiSeenVersion) AiRefreshScenes();
        UpdateAiStatus();
    }

    /// <summary>A seek: if it lands in a part that isn't scanned yet, the scan moves there.</summary>
    private void AiOnSeek(long ms) => _aiScanner?.Focus(ms);

    private void AiRefreshScenes()
    {
        if (_aiScan == null)
        {
            _aiScenes = new List<AiScene>();
            _aiSkips = new List<SkipRange>();
        }
        else
        {
            _aiSeenVersion = _aiScan.Version;
            var length = _aiScan.LengthMs > 0 ? _aiScan.LengthMs : _lastLength;
            _aiScenes = SceneFinder.Find(_aiScan.Snapshot(), AiKinds, _settings.AiSensitivity, _aiScan.Ignored, length, _aiScan.Edits);
            _aiSkips = _aiScenes.Where(s => !s.Ignored).Select(s => new SkipRange { StartMs = s.StartMs, EndMs = s.EndMs }).ToList();
        }
        _skipMarksLength = -1;
        DrawSkipMarks();
    }

    private void AiCloseMedia()
    {
        AiStop();
        _aiScan = null;
        _aiLastStatus = null;
        AiRefreshScenes();
    }

    /// <summary>On exit: stop and give the scan a moment to save.</summary>
    private void AiShutdown()
    {
        var scanner = _aiScanner;
        AiStop();
        scanner?.Wait(TimeSpan.FromSeconds(3));
    }

    // ---- status --------------------------------------------------------------------------

    /// <summary>The scanner's status, short enough for the pill on the video (the menu shows it in full).</summary>
    private static string ShortStatus(string status)
    {
        var cut = status.IndexOf("… the first time", StringComparison.Ordinal);
        if (cut > 0) status = status.Substring(0, cut) + "…";
        return status.Length > 72 ? status.Substring(0, 71) + "…" : status;
    }

    private static string ShortDevice(string? device) =>
        string.IsNullOrEmpty(device) ? "" : device!.Split(new[] { " (" }, StringSplitOptions.None)[0];

    private string AiStatusLine()
    {
        if (!AiEngine.IsSupported) return AiEngine.UnsupportedReason ?? "Not available on this PC";
        if (_aiScan == null) return _current == null ? "Open a video to scan it" : "This file can't be scanned (stream or audio)";
        var percent = (int)Math.Floor(_aiScan.CoveredFraction * 100);
        if (_aiScanner is { } s)
        {
            if (s.Status != "Scanning…") return s.Status;
            var speed = s.Speed > 0.5 ? $" at {s.Speed:0}x" : "";
            var ms = s.MillisecondsPerFrame > 0 ? $" · {s.MillisecondsPerFrame:0} ms per picture" : "";
            return $"Scanning {percent}%{speed} on {s.Device}{ms}";
        }
        if (_aiScan.IsComplete) return $"Scanned{(_aiScan.Device != null ? " on " + _aiScan.Device : "")}";
        if (_aiLastStatus != null && _aiLastStatus != "Done" && _aiLastStatus != "Stopped") return _aiLastStatus;
        return percent > 0 ? $"{percent}% scanned" : "Not scanned yet";
    }

    private void UpdateAiStatus()
    {
        string? pill = null;
        if (_aiScanner is { } s && _aiScan != null)
        {
            var percent = (int)Math.Floor(_aiScan.CoveredFraction * 100);
            pill = s.Status == "Scanning…"
                ? $"AI scan {percent}%{(s.Speed > 0.5 ? $" · {s.Speed:0}x" : "")} · {ShortDevice(s.Device)}"
                : ShortStatus(s.Status);
        }
        AiPill.Visibility = pill == null ? Visibility.Collapsed : Visibility.Visible;
        AiPillText.Text = pill ?? "";

        if (_aiMenuStatus != null && SettingsItems.Children.Contains(_aiMenuStatus))
            _aiMenuStatus.Text = AiStatusLine();
    }

    private string AiMenuValue()
    {
        if (!AiEngine.IsSupported) return "Not available";
        if (!_settings.AiFilter) return "Off";
        if (_aiScanner != null && _aiScan != null) return $"Scanning {(int)Math.Floor(_aiScan.CoveredFraction * 100)}%";
        if (_aiScan == null) return "On";
        if (_aiSkips.Count > 0) return $"{_aiSkips.Count} to skip";
        return _aiScan.IsComplete ? "Nothing found" : "On";
    }

    private string AiAboutText() =>
        AiEngine.IsSupported
            ? $"runs on this PC ({AiEngine.ActiveDevice ?? "NPU, GPU or CPU"}) with CLIP ViT-B/32 (OpenAI, MIT){(_settings.AiNudeNet ? " and NudeNet (AGPL-3.0)" : "")}"
            : "not available on this PC";

    private static string SensitivityLabel(AiSensitivity s) => s switch
    {
        AiSensitivity.Low => "Low",
        AiSensitivity.High => "High",
        _ => "Medium",
    };

    private static string ProcessorLabel(AiProcessor p) => p switch
    {
        AiProcessor.Npu => "NPU",
        AiProcessor.Gpu => "GPU",
        AiProcessor.Cpu => "CPU",
        _ => "Auto",
    };

    // ---- settings menu ---------------------------------------------------------------------

    private void BuildAiMenu()
    {
        SettingsItems.Children.Clear();
        SettingsItems.Children.Add(MenuRow("Icon.ChevronLeft", "AI scene filter", null, false, BuildSettingsMain, bold: true));
        SettingsItems.Children.Add(MenuSeparator());

        if (!AiEngine.IsSupported)
        {
            SettingsItems.Children.Add(MenuNote(AiEngine.UnsupportedReason ?? "Not available on this PC."));
            PositionSettings();
            return;
        }

        SettingsItems.Children.Add(MenuRow(null, "Skip scenes the AI finds", null, false, () =>
        {
            _settings.AiFilter = !_settings.AiFilter;
            AiRefreshScenes();
            BuildAiMenu();
        }, toggle: _settings.AiFilter));
        SettingsItems.Children.Add(MenuRow(null, "Nudity and sex", null, false, () =>
        {
            _settings.AiSkipSexual = !_settings.AiSkipSexual;
            AiRefreshScenes();
            BuildAiMenu();
        }, toggle: _settings.AiSkipSexual));
        SettingsItems.Children.Add(MenuRow(null, "Kissing", null, false, () =>
        {
            _settings.AiSkipKissing = !_settings.AiSkipKissing;
            AiRefreshScenes();
            BuildAiMenu();
        }, toggle: _settings.AiSkipKissing));
        SettingsItems.Children.Add(MenuRow(null, "Sensitivity", SensitivityLabel(_settings.AiSensitivity), true, BuildAiSensitivityMenu));
        SettingsItems.Children.Add(MenuRow(null, "Scan videos when they open", null, false, () =>
        {
            _settings.AiAutoScan = !_settings.AiAutoScan;
            if (_settings.AiAutoScan) AiStartIfNeeded(_lastTime);
            BuildAiMenu();
        }, toggle: _settings.AiAutoScan));
        SettingsItems.Children.Add(MenuRow(null, "Processor", ProcessorLabel(_settings.AiProcessor), true, BuildAiProcessorMenu));
        SettingsItems.Children.Add(MenuRow(null, "Extra nudity detector (NudeNet)", null, false, () =>
        {
            _settings.AiNudeNet = !_settings.AiNudeNet;
            AiModelsChanged();
            BuildAiMenu();
        }, toggle: _settings.AiNudeNet));

        SettingsItems.Children.Add(MenuSeparator());
        _aiMenuStatus = MenuNote(AiStatusLine());
        SettingsItems.Children.Add(_aiMenuStatus);

        if (AiCanScan(_current))
        {
            if (_aiScanner != null)
                SettingsItems.Children.Add(MenuRow(null, "Stop scanning", null, false, () =>
                {
                    AiStop();
                    BuildAiMenu();
                }));
            else if (_aiScan is { IsComplete: true })
                SettingsItems.Children.Add(MenuRow(null, "Scan this video again", null, false, () =>
                {
                    AiStartScan(_lastTime, fresh: true);
                    BuildAiMenu();
                }));
            else
                SettingsItems.Children.Add(MenuRow(null, "Scan this video now", null, false, () =>
                {
                    AiStartScan(_lastTime, fresh: false);
                    BuildAiMenu();
                }));
        }

        foreach (var scene in _aiScenes.ToList())
        {
            var label = $"{FormatTime(scene.StartMs, _lastLength)} – {FormatTime(scene.EndMs, _lastLength)}  ·  {scene.KindText}";
            SettingsItems.Children.Add(MenuRow(scene.Ignored ? null : "Icon.FastForward", label, scene.Ignored ? "Skip it" : "Don't skip", false, () =>
            {
                if (_aiScan == null) return;
                if (scene.Ignored) _aiScan.Unignore(scene.OriginalRange);
                else _aiScan.Ignore(scene.OriginalRange);
                var scan = _aiScan;
                _ = Task.Run(scan.Save);
                AiRefreshScenes();
                BuildAiMenu();
            }));
        }
        if (AiCanScan(_current))
            SettingsItems.Children.Add(MenuRow(null, "Scenes to skip…", ScenesMenuValue(), false, () => ShowScenesDialog()));
        if (_aiScenes.Count > 0)
            SettingsItems.Children.Add(MenuNote("Move a scene's start or end, watch it once, or add one the AI missed in Scenes to skip."));
        PositionSettings();
    }

    private void BuildAiSensitivityMenu()
    {
        SettingsItems.Children.Clear();
        SettingsItems.Children.Add(MenuRow("Icon.ChevronLeft", "Sensitivity", null, false, BuildAiMenu, bold: true));
        SettingsItems.Children.Add(MenuSeparator());
        foreach (var (value, text) in new[]
                 {
                     (AiSensitivity.Low, "Low · only clear scenes"),
                     (AiSensitivity.Medium, "Medium"),
                     (AiSensitivity.High, "High · also skips borderline scenes"),
                 })
        {
            SettingsItems.Children.Add(MenuRow(_settings.AiSensitivity == value ? "Icon.Check" : null, text, null, false, () =>
            {
                _settings.AiSensitivity = value;
                AiRefreshScenes();
                BuildAiMenu();
            }));
        }
        SettingsItems.Children.Add(MenuNote("Changing this doesn't need a new scan."));
        PositionSettings();
    }

    private void BuildAiProcessorMenu()
    {
        SettingsItems.Children.Clear();
        SettingsItems.Children.Add(MenuRow("Icon.ChevronLeft", "Processor", null, false, BuildAiMenu, bold: true));
        SettingsItems.Children.Add(MenuSeparator());
        foreach (var (value, text) in new[]
                 {
                     (AiProcessor.Auto, "Auto · NPU, else GPU, else CPU"),
                     (AiProcessor.Npu, "NPU"),
                     (AiProcessor.Gpu, "GPU"),
                     (AiProcessor.Cpu, "CPU"),
                 })
        {
            SettingsItems.Children.Add(MenuRow(_settings.AiProcessor == value ? "Icon.Check" : null, text, null, false, () =>
            {
                if (_settings.AiProcessor != value)
                {
                    _settings.AiProcessor = value;
                    AiModelsChanged();
                }
                BuildAiMenu();
            }));
        }
        var device = _aiScanner?.Device ?? AiEngine.ActiveDevice;
        SettingsItems.Children.Add(MenuNote(device != null ? "Now running on: " + device : "Falls back to the next one if a chip can't run the models."));
        PositionSettings();
    }

    /// <summary>The models or the chip changed: a running scan restarts with them (and the cache key changes with NudeNet).</summary>
    private void AiModelsChanged()
    {
        var wasScanning = _aiScanner != null;
        AiStop();
        if (_current != null && AiCanScan(_current))
            _aiScan = SceneScan.Load(_current.Source, AiEngine.ModelSetFor(_settings.AiNudeNet));
        AiRefreshScenes();
        if (wasScanning || _settings.AiAutoScan) AiStartIfNeeded(_lastTime);
    }

    private static TextBlock MenuNote(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
        Margin = new Thickness(18, 4, 18, 8),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 320,
    };

    // ---- right-click menu --------------------------------------------------------------------

    private MenuItem AiContextMenu()
    {
        var ai = new MenuItem { Header = "AI scene filter" };
        if (!AiEngine.IsSupported)
        {
            ai.IsEnabled = false;
            return ai;
        }
        ai.Items.Add(MenuItem("Skip scenes the AI finds", null, () => { _settings.AiFilter = !_settings.AiFilter; AiRefreshScenes(); }, _settings.AiFilter));
        ai.Items.Add(MenuItem("Nudity and sex", null, () => { _settings.AiSkipSexual = !_settings.AiSkipSexual; AiRefreshScenes(); }, _settings.AiSkipSexual));
        ai.Items.Add(MenuItem("Kissing", null, () => { _settings.AiSkipKissing = !_settings.AiSkipKissing; AiRefreshScenes(); }, _settings.AiSkipKissing));
        if (AiCanScan(_current))
        {
            ai.Items.Add(new Separator());
            if (_aiScanner != null) ai.Items.Add(MenuItem("Stop scanning", null, AiStop));
            else ai.Items.Add(MenuItem(_aiScan is { IsComplete: true } ? "Scan this video again" : "Scan this video now", null,
                () => AiStartScan(_lastTime, fresh: _aiScan is { IsComplete: true })));
            ai.Items.Add(MenuItem("Scenes to skip…", null, () => ShowScenesDialog()));
        }
        return ai;
    }
}
