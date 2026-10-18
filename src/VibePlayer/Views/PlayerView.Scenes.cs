using VibePlayer.Core;
using VibePlayer.Core.Ai;

namespace VibePlayer.Views;

/// <summary>
/// "Scenes to skip": one list of the scenes the AI found (pink) and the parts you marked (amber). From there you keep or
/// skip an AI scene, move any scene's start and end, watch a scene once without skipping it, or add one the AI missed.
/// AI scenes keep their moved edges in the file's scan (so a rescan or another sensitivity keeps them); your own scenes
/// are the same skips as Settings › Skip scenes.
/// </summary>
public partial class PlayerView
{
    /// <summary>"Watch it once": this part plays without being skipped until playback passes it or moves away.</summary>
    private TimeRange? _watchOnce;

    private void ShowScenesDialog(string? selectKey = null)
    {
        if (!CanEditSkips()) return;
        CloseSettings();
        new ScenesDialog(new ScenesHost(this), selectKey) { Owner = _host.HostWindow }.ShowDialog();
    }

    /// <summary>The number of scenes for the menus ("3", or "None").</summary>
    private string ScenesMenuValue()
    {
        var n = _aiScenes.Count + _skips.Count;
        return n == 0 ? "None" : n.ToString();
    }

    /// <summary>Called from CheckSkip: is this part being watched once (and should not be skipped)?</summary>
    private bool WatchingOnce(SkipRange range, long time)
    {
        if (_watchOnce is not { } w) return false;
        if (time >= w.EndMs || time < w.StartMs - 15_000)
        {
            _watchOnce = null;
            return false;
        }
        return range.StartMs < w.EndMs && range.EndMs > w.StartMs;
    }

    private void WatchOnce(long startMs, long endMs)
    {
        _watchOnce = new TimeRange(startMs, endMs);
        SeekTo(Math.Max(0, startMs - 3000));
        if (_hasMedia && !_engine.IsPlaying) TogglePlay(false);
        ShowInfo("Icon.Play", $"Playing {FormatTime(startMs, _lastLength)} – {FormatTime(endMs, _lastLength)} once, without skipping it");
    }

    private static List<SkipRange> MergeSkips(IEnumerable<SkipRange> ranges)
    {
        var merged = new List<SkipRange>();
        foreach (var r in ranges.OrderBy(r => r.StartMs))
        {
            if (merged.Count > 0 && r.StartMs <= merged[^1].EndMs)
                merged[^1].EndMs = Math.Max(merged[^1].EndMs, r.EndMs);
            else
                merged.Add(new SkipRange { StartMs = r.StartMs, EndMs = r.EndMs });
        }
        return merged;
    }

    private static string OwnKey(SkipRange r) => $"you:{r.StartMs}:{r.EndMs}";

    private static string AiKey(AiScene s) => $"ai:{s.OriginalStartMs}";

    /// <summary>The player, as the Scenes window sees it.</summary>
    private sealed class ScenesHost : IScenesHost
    {
        private readonly PlayerView _p;

        public ScenesHost(PlayerView player) => _p = player;

        public long LengthMs => _p._lastLength;
        public long TimeMs => _p._seekTargetMs >= 0 ? _p._seekTargetMs : _p._lastTime;
        public bool AiSkipping => _p._settings.AiFilter;
        public bool OwnSkipping => _p._settings.SkipEnabled;

        public IReadOnlyList<SceneListItem> Scenes() =>
            _p._aiScenes.Select(s => new SceneListItem
            {
                Key = AiKey(s), FromAi = true, Kind = s.KindText, StartMs = s.StartMs, EndMs = s.EndMs,
                Skipped = !s.Ignored, Edited = s.Edited, OriginalStartMs = s.OriginalStartMs, OriginalEndMs = s.OriginalEndMs,
            }).Concat(_p._skips.Select(r => new SceneListItem
            {
                Key = OwnKey(r), Kind = "Added by you", StartMs = r.StartMs, EndMs = r.EndMs,
                OriginalStartMs = r.StartMs, OriginalEndMs = r.EndMs,
            })).ToList();

        public string? SetTimes(SceneListItem scene, long startMs, long endMs)
        {
            if (scene.FromAi)
            {
                if (_p._aiScan is not { } scan || Ai(scene) is not { } ai) return null;
                scan.SetEdit(ai.OriginalRange, new TimeRange(startMs, endMs));
                SaveScan();
                return scene.Key;
            }

            var old = Own(scene);
            if (old == null) return null;
            var updated = new SkipRange { StartMs = startMs, EndMs = endMs };
            _p._skips = MergeSkips(_p._skips.Where(r => !ReferenceEquals(r, old)).Append(updated));
            _p.SaveSkips();
            var now = _p._skips.FirstOrDefault(r => r.StartMs <= startMs && r.EndMs >= endMs);
            return now == null ? null : OwnKey(now);
        }

        public void SetSkipped(SceneListItem scene, bool skip)
        {
            if (!scene.FromAi || _p._aiScan is not { } scan || Ai(scene) is not { } ai) return;
            if (skip) scan.Unignore(ai.OriginalRange);
            else scan.Ignore(ai.OriginalRange);
            SaveScan();
        }

        public void ResetTimes(SceneListItem scene)
        {
            if (!scene.FromAi || _p._aiScan is not { } scan || Ai(scene) is not { } ai) return;
            scan.ClearEdit(ai.OriginalRange);
            SaveScan();
        }

        public void Remove(SceneListItem scene)
        {
            if (scene.FromAi)
            {
                SetSkipped(scene, false); // an AI scene can't be deleted (a rescan would find it again): it is kept
                return;
            }
            if (Own(scene) is not { } old) return;
            _p._skips.Remove(old);
            _p.SaveSkips();
        }

        public string? Add(long startMs, long endMs)
        {
            if (!_p.CanEditSkips() || endMs <= startMs) return null;
            _p._skips = MergeSkips(_p._skips.Append(new SkipRange { StartMs = startMs, EndMs = endMs }));
            _p.SaveSkips();
            var now = _p._skips.FirstOrDefault(r => r.StartMs <= startMs && r.EndMs >= endMs);
            return now == null ? null : OwnKey(now);
        }

        public void Watch(SceneListItem scene) => _p.WatchOnce(scene.StartMs, scene.EndMs);

        private AiScene? Ai(SceneListItem scene) => _p._aiScenes.FirstOrDefault(s => AiKey(s) == scene.Key);

        private SkipRange? Own(SceneListItem scene) => _p._skips.FirstOrDefault(r => OwnKey(r) == scene.Key);

        private void SaveScan()
        {
            if (_p._aiScan is { } scan) _ = Task.Run(scan.Save);
            _p._lastSkipEndMs = -1;
            _p.AiRefreshScenes();
        }
    }
}
