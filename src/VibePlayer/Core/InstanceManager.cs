using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using VibePlayer.Interop;

namespace VibePlayer.Core;

/// <summary>
/// Lets several player windows run side by side and routes newly opened files:
/// every window listens on its own named pipe, and a new launch can hand its files to the
/// most recently used window instead of opening another one.
/// </summary>
internal static class InstanceManager
{
    private static readonly string User = Environment.UserName;
    private static string LaunchName => AppInfo.Id + "-launch-" + User;
    private static string InstancePipe(int pid) => $"{AppInfo.Id}-{User}-{pid}";

    private static readonly object Gate = new();
    private static Mutex? s_launchMutex;
    private static CancellationTokenSource? s_launchCts;
    private static Action<string[]>? s_launchHandler;
    private static readonly List<string[]> s_launchBacklog = new();

    public static string[] Normalize(IEnumerable<string> args) =>
        args.Select(a => a.Trim().Trim('"'))
            .Where(a => a.Length > 0 && !a.StartsWith("--", StringComparison.Ordinal))
            .Select(a =>
            {
                if (MediaFormats.LooksLikeUrl(a)) return a;
                try { return Path.GetFullPath(a); } catch { return a; }
            })
            .ToArray();

    // ------------------------------------------------------------------ launch batching

    /// <summary>
    /// Explorer starts one process per selected file. The first one becomes the leader and the
    /// others hand it their files and exit, so a multi-file "Open" gives one prompt and one playlist.
    /// Returns false when our files were handed to a leader (this process should exit).
    /// </summary>
    public static bool TryLeadLaunch(string[] args)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var mutex = new Mutex(false, @"Local\" + LaunchName);
            bool owned;
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }

            if (owned)
            {
                s_launchMutex = mutex;
                s_launchCts = new CancellationTokenSource();
                StartServer(LaunchName, OnLaunchMessage, s_launchCts.Token);
                return true;
            }

            mutex.Dispose();
            if (Send(LaunchName, args, 1500)) return false;
        }
        return true; // could not reach the leader - act on our own
    }

    /// <summary>Where files from follower processes go from now on (files that already arrived are delivered at once).</summary>
    public static void SetLaunchHandler(Action<string[]> handler)
    {
        List<string[]> backlog;
        lock (Gate)
        {
            s_launchHandler = handler;
            backlog = s_launchBacklog.ToList();
            s_launchBacklog.Clear();
        }
        foreach (var args in backlog) handler(args);
    }

    private static void OnLaunchMessage(string[] args)
    {
        Action<string[]>? handler;
        lock (Gate)
        {
            handler = s_launchHandler;
            if (handler == null)
            {
                s_launchBacklog.Add(args);
                return;
            }
        }
        handler(args);
    }

    /// <summary>Stops collecting follower files. Must run on the thread that called <see cref="TryLeadLaunch"/>.</summary>
    public static void EndLaunch()
    {
        s_launchCts?.Cancel();
        if (s_launchMutex == null) return;
        try { s_launchMutex.ReleaseMutex(); } catch { /* not owned */ }
        s_launchMutex.Dispose();
        s_launchMutex = null;
    }

    // ------------------------------------------------------------------ windows

    public static void ListenAsWindow(Action<string[]> onMessage) =>
        StartServer(InstancePipe(Compat.ProcessId), onMessage, CancellationToken.None);

    /// <summary>The most recently used window of another process (top of the Z-order), if any.</summary>
    public static (int Pid, string Title)? FindOpenWindow()
    {
        var self = Compat.ProcessId;
        int session;
        using (var me = Process.GetCurrentProcess()) session = me.SessionId;

        var pids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(AppInfo.Id))
        {
            using (p)
            {
                try
                {
                    if (p.Id != self && p.SessionId == session) pids.Add(p.Id);
                }
                catch
                {
                    // process exited meanwhile
                }
            }
        }
        if (pids.Count == 0) return null;

        (int, string)? found = null;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains((int)pid)) return true;
            var text = new StringBuilder(512);
            Native.GetWindowText(hwnd, text, text.Capacity);
            found = ((int)pid, text.ToString());
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static bool SendToWindow(int pid, string[] args)
    {
        Native.AllowSetForegroundWindow(pid);
        return Send(InstancePipe(pid), args, 2000);
    }

    // ------------------------------------------------------------------ pipes

    private static bool Send(string pipeName, string[] args, int timeoutMs)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", args));
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void StartServer(string pipeName, Action<string[]> onMessage, CancellationToken token)
    {
        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(pipeName, PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = reader.ReadToEnd();
                    onMessage(Compat.SplitLines(text));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Pipe " + pipeName);
                    Thread.Sleep(150);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Pipe " + pipeName,
        };
        thread.Start();
    }
}
