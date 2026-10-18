using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VibePlayer.Core;

namespace VibePlayer.Views;

/// <summary>
/// VLC-style automatic updates: a quiet check shortly after start-up and then at most once a day,
/// a non-modal card when a newer version is out, and "Update now" = download, verify, install, restart.
/// </summary>
public partial class PlayerView
{
    private const string DownloadPageUrl = AppInfo.DownloadPageUrl;
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private DispatcherTimer? _updateTimer;
    private UpdateManifest? _update;           // the version on offer (card shown, or waiting)
    private bool _updateDeferred;              // found while watching full screen: shown later
    private bool _updateAskedFor;              // the user asked (Check for updates): don't tuck the card away
    private bool _checkingUpdates;
    private CancellationTokenSource? _downloadCts;

    private void InitUpdates()
    {
        if (!AppInfo.UpdatesEnabled) return;

        _updateTimer = new DispatcherTimer { Interval = FirstCheckDelay };
        _updateTimer.Tick += (_, _) =>
        {
            // First tick 10 s after start-up, then hourly; a real check happens at most once every 24 h.
            _updateTimer.Interval = TimeSpan.FromHours(1);
            if (_settings.CheckForUpdates) _ = CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();

        UpdateNowButton.Click += (_, _) => _ = UpdateNowAsync();
        UpdateLaterButton.Click += (_, _) => HideUpdateCard();
        UpdateCloseButton.Click += (_, _) =>
        {
            if (_downloadCts != null) _downloadCts.Cancel();
            else HideUpdateCard();
        };
        UpdateSkipButton.Click += (_, _) =>
        {
            if (_update == null) return;
            _settings.SkippedVersion = _update.Version;
            _settings.Save();
            var version = _update.Version;
            HideUpdateCard();
            ShowToast($"{AppInfo.Name} {version} skipped. You can still get it from Settings › Updates › Check for updates.", 5);
        };
        UpdateCancelButton.Click += (_, _) => _downloadCts?.Cancel();
    }

    /// <summary>Checks the feed. Automatic checks are silent unless there's something new; manual ones always answer.</summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingUpdates || _downloadCts != null) return;

        if (!manual)
        {
            var last = AppSettings.ReadLastUpdateCheck() ?? _settings.LastUpdateCheck;
            var now = DateTime.UtcNow;
            if (last is { } at && at <= now && now - at < CheckInterval) return;
        }

        _checkingUpdates = true;
        if (manual) ShowToast("Checking for updates…", 20);
        try
        {
            UpdateManifest manifest;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                manifest = await Updater.FetchManifestAsync(cts.Token);

            _settings.LastUpdateCheck = DateTime.UtcNow;
            _settings.Save();

            if (!Updater.IsNewer(manifest))
            {
                if (manual) ShowToast($"You're up to date ({AppInfo.Name} {Updater.CurrentVersionText})");
                return;
            }

            // A skipped version is only offered again when asked for.
            var skipped = Updater.ParseVersion(_settings.SkippedVersion) is { } s && s == Updater.ParseVersion(manifest.Version);
            if (skipped && !manual) return;

            if (manual) Fade(Toast, 0, 150);
            OfferUpdate(manifest, manual);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update check (" + Updater.FeedUrl + ")");
            if (manual) ShowToast("Couldn't check for updates. Check your internet connection and try again.", 5);
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    private void OfferUpdate(UpdateManifest manifest, bool manual)
    {
        _update = manifest;
        _updateAskedFor = manual;
        if (!manual && MustNotInterrupt)
        {
            _updateDeferred = true; // shown once the video is paused or full screen ends (OnTick)
            return;
        }
        ShowUpdateCard();
    }

    /// <summary>Never pop up over a full-screen video that is playing, or over the small miniplayer.</summary>
    private bool MustNotInterrupt =>
        _host.IsMiniPlayer || (_host.IsFullscreen && _hasMedia && !_ended && (_engine.IsPlaying || _engine.IsLoading));

    /// <summary>Runs with the UI tick: shows a waiting offer when it's a good moment, and tucks an unasked-for card away during full-screen playback.</summary>
    private void UpdateCardTick()
    {
        if (_downloadCts != null) return;
        if (UpdateCard.Visibility == Visibility.Visible)
        {
            if (!_updateAskedFor && MustNotInterrupt)
            {
                UpdateCard.Visibility = Visibility.Collapsed;
                _updateDeferred = true;
            }
            return;
        }
        if (_updateDeferred) ShowDeferredUpdate();
    }

    private void ShowDeferredUpdate()
    {
        if (_update == null)
        {
            _updateDeferred = false;
            return;
        }
        if (MustNotInterrupt) return;
        _updateDeferred = false;
        ShowUpdateCard();
    }

    private void ShowUpdateCard()
    {
        var manifest = _update;
        if (manifest == null) return;

        UpdateTitle.Text = $"{AppInfo.Name} {manifest.Version} is available";
        var subtitle = $"You have {Updater.CurrentVersionText}";
        if (DateTime.TryParseExact(manifest.Released, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var released))
            subtitle += "  ·  released " + released.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        UpdateSubtitle.Text = subtitle;

        UpdateNotes.Children.Clear();
        foreach (var note in manifest.Notes.Take(6))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            var bullet = new TextBlock { Text = "•", FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)), Margin = new Thickness(2, 0, 9, 0) };
            DockPanel.SetDock(bullet, Dock.Left);
            row.Children.Add(bullet);
            row.Children.Add(new TextBlock { Text = note, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), TextWrapping = TextWrapping.Wrap });
            UpdateNotes.Children.Add(row);
        }
        UpdateNotes.Visibility = UpdateNotes.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        SetUpdateCardBusy(false);
        UpdateNowButton.Content = CanInstallUpdate(manifest) ? "Update now" : "Download page";
        UpdateStatus.Visibility = Visibility.Collapsed;

        UpdateCard.Width = Math.Min(400, Math.Max(260, Root.ActualWidth - 32));
        UpdateCard.Visibility = Visibility.Visible;
        UpdateCard.Opacity = 0;
        Fade(UpdateCard, 1, 200);
    }

    private void HideUpdateCard()
    {
        _updateDeferred = false;
        UpdateCard.Visibility = Visibility.Collapsed;
        RestartHideTimer();
    }

    /// <summary>The installer can only update a copy that it installed (and only with an installer for this edition).</summary>
    private static bool CanInstallUpdate(UpdateManifest manifest) =>
        manifest.ForThisEdition != null && Updater.DetectInstallScope() != InstallScope.NotInstalled;

    private void SetUpdateCardBusy(bool busy)
    {
        UpdateProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateNowButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        UpdateLaterButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        UpdateSkipButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowUpdateError(string text)
    {
        UpdateStatus.Text = text;
        UpdateStatus.Visibility = Visibility.Visible;
    }

    private void SetUpdateProgress(double fraction, string text)
    {
        UpdateProgressFill.Width = Math.Max(0, UpdateProgressTrack.ActualWidth * MathEx.Clamp(fraction, 0, 1));
        UpdateProgressText.Text = text;
    }

    private async Task UpdateNowAsync()
    {
        var manifest = _update;
        if (manifest == null || _downloadCts != null) return;

        var file = manifest.ForThisEdition;
        var scope = Updater.DetectInstallScope();
        if (file == null || scope == InstallScope.NotInstalled)
        {
            // Not installed by the installer (or no installer for this edition): let the user download it.
            OpenInBrowser(string.IsNullOrWhiteSpace(manifest.Page) ? DownloadPageUrl : manifest.Page!);
            HideUpdateCard();
            return;
        }

        UpdateStatus.Visibility = Visibility.Collapsed;
        SetUpdateCardBusy(true);
        var sizeMb = file.Size / (1024.0 * 1024.0);
        SetUpdateProgress(0, $"Downloading {AppInfo.Name} {manifest.Version}…  0%  ({sizeMb:0.0} MB)");

        _downloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(f =>
                SetUpdateProgress(f, $"Downloading {AppInfo.Name} {manifest.Version}…  {f * 100:0}%  ({sizeMb:0.0} MB)"));
            var installer = await Updater.DownloadAsync(manifest, file, progress, _downloadCts.Token);

            SetUpdateProgress(1, scope == InstallScope.AllUsers
                ? $"Verified. Installing… Windows may ask for permission. {AppInfo.Name} restarts by itself."
                : $"Verified. Installing… {AppInfo.Name} restarts by itself.");
            UpdateCancelButton.Visibility = Visibility.Collapsed;
            Updater.LaunchInstaller(installer, scope);

            // Close so the installer can replace our files (it starts the app again when it's done).
            await Task.Delay(400);
            _host.HostWindow.Close();
        }
        catch (OperationCanceledException)
        {
            SetUpdateCardBusy(false);
            ShowUpdateError("Download cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update download / install");
            SetUpdateCardBusy(false);
            UpdateNowButton.Content = "Try again";
            ShowUpdateError(ex is UpdateVerificationException
                ? ex.Message + " The file was deleted."
                : "Couldn't download the update: " + ex.Message);
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Open " + url);
            ShowToast("Couldn't open " + url, 5);
        }
    }

    // ---- settings menu ----------------------------------------------------------------------

    private string UpdatesMenuValue() =>
        _update != null && Updater.IsNewer(_update) ? $"{_update.Version} available" : Updater.CurrentVersionText;

    private void BuildUpdatesMenu()
    {
        SettingsItems.Children.Clear();
        SettingsItems.Children.Add(MenuRow("Icon.ChevronLeft", "Updates", null, false, BuildSettingsMain, bold: true));
        SettingsItems.Children.Add(MenuSeparator());

        SettingsItems.Children.Add(MenuRow(null, "Check for updates", _checkingUpdates ? "Checking…" : null, false, () =>
        {
            CloseSettings();
            _ = CheckForUpdatesAsync(manual: true);
        }));
        SettingsItems.Children.Add(MenuRow(null, "Check automatically", null, false, () =>
        {
            _settings.CheckForUpdates = !_settings.CheckForUpdates;
            _settings.Save();
            BuildUpdatesMenu();
        }, toggle: _settings.CheckForUpdates));

        var last = _settings.LastUpdateCheck is { } at
            ? "Last checked " + at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : "Not checked yet";
        SettingsItems.Children.Add(new TextBlock
        {
            Text = $"{AppInfo.Name} {Updater.CurrentVersionText} preview ({EditionName(Updater.Edition)})  ·  {last}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
            Margin = new Thickness(18, 6, 18, 8),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 300,
        });
        PositionSettings();
    }

    private static string EditionName(string edition) => edition switch
    {
        "x64" => "Windows 10/11, 64-bit",
        "arm64" => "Windows 10/11 on ARM",
        "x86" => "Windows 10, 32-bit",
        "win7-8" => "Windows 7/8.1",
        _ => edition,
    };
}
