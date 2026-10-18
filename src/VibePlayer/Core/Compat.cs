using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VibePlayer.Core;

/// <summary>
/// Small helpers that behave the same on .NET 10 (Windows 10/11 editions) and .NET Framework 4.8
/// (Windows 7/8.1 edition), so the rest of the code is shared unchanged.
/// </summary>
internal static class Compat
{
    public static int ProcessId
    {
        get
        {
#if NETFRAMEWORK
            using var p = Process.GetCurrentProcess();
            return p.Id;
#else
            return Environment.ProcessId;
#endif
        }
    }

    /// <summary>Moves <paramref name="source"/> to <paramref name="target"/>, replacing it if it exists.</summary>
    public static void MoveOverwrite(string source, string target)
    {
#if NETFRAMEWORK
        // Same call .NET 10's File.Move(source, target, overwrite: true) makes.
        if (!MoveFileEx(source, target, MOVEFILE_REPLACE_EXISTING | MOVEFILE_COPY_ALLOWED))
            throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message + " (" + target + ")");
#else
        File.Move(source, target, overwrite: true);
#endif
    }

    /// <summary>Splits on '\n', trims every line and drops the empty ones.</summary>
    public static string[] SplitLines(string text)
    {
#if NETFRAMEWORK
        return text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
#else
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
#endif
    }

    /// <summary>
    /// Every file below <paramref name="dir"/> (recursively), skipping hidden/system entries and folders
    /// that can't be read - like <c>new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }</c>.
    /// </summary>
    public static IEnumerable<string> EnumerateFilesRecursive(string dir)
    {
#if NETFRAMEWORK
        const FileAttributes skip = FileAttributes.Hidden | FileAttributes.System;
        var pending = new Queue<string>();
        pending.Enqueue(dir);
        while (pending.Count > 0)
        {
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(pending.Dequeue()).GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var attributes = entry.Attributes;
                if ((attributes & skip) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // Like .NET 10, don't follow junctions / symbolic links into other folders.
                    if ((attributes & FileAttributes.ReparsePoint) == 0) pending.Enqueue(entry.FullName);
                }
                else
                {
                    yield return entry.FullName;
                }
            }
        }
#else
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(dir, "*", options);
#endif
    }

#if NETFRAMEWORK
    private const uint MOVEFILE_REPLACE_EXISTING = 0x1;
    private const uint MOVEFILE_COPY_ALLOWED = 0x2;

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
#endif
}

#if NETFRAMEWORK
/// <summary>.NET Core library methods the shared code uses that .NET Framework 4.8 lacks.</summary>
internal static class LegacyExtensions
{
    // Lets "foreach (var (key, value) in dictionary)" compile on .NET Framework.
    public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
    {
        key = pair.Key;
        value = pair.Value;
    }

    // string.Split(string separator)
    public static string[] Split(this string text, string separator) =>
        text.Split(new[] { separator }, StringSplitOptions.None);

    // Enumerable.FirstOrDefault / LastOrDefault with a default value (.NET 6+)
    public static T FirstOrDefault<T>(this IEnumerable<T> source, Func<T, bool> predicate, T defaultValue)
    {
        foreach (var item in source)
            if (predicate(item)) return item;
        return defaultValue;
    }

    public static T LastOrDefault<T>(this IEnumerable<T> source, Func<T, bool> predicate, T defaultValue)
    {
        var last = defaultValue;
        foreach (var item in source)
            if (predicate(item)) last = item;
        return last;
    }
}
#endif
