using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

// Unity's Auto Refresh only runs when the editor regains focus. This watches Assets/ and Packages/
// and refreshes (and therefore compiles) while the editor is in the background. When the editor is
// focused, Unity's own Auto Refresh handles changes, so this stays idle.
[InitializeOnLoad]
internal static class BackgroundAutoRefresh
{
    private const long QuietTicks = TimeSpan.TicksPerMillisecond * 500;
    private const int WatcherBufferSize = 64 * 1024;
    private const long RetryTicks = TimeSpan.TicksPerSecond * 2;
    private static FileSystemWatcher[] watchers;
    private static long lastChangeTicks;
    private static int pending;
    private static int watcherFailed;
    // Main thread only.
    private static long nextRetryTicks;
    private static bool failureLogged;

    static BackgroundAutoRefresh()
    {
        // Asset import worker processes also run [InitializeOnLoad]; only the main editor should watch.
        if (AssetDatabase.IsAssetImportWorkerProcess() == true) return;
        EditorApplication.update += Tick;
        AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
        EditorApplication.quitting += Shutdown;
        if (TryCreateWatchers() == false) ScheduleRetry();
    }

    // Builds every watcher before publishing any of them, so a partial failure leaves nothing behind.
    private static bool TryCreateWatchers()
    {
        string root = Path.GetDirectoryName(Application.dataPath);
        string assets = Path.Combine(root, "Assets");
        string packages = Path.Combine(root, "Packages");
        FileSystemWatcher assetsWatcher = null;
        try
        {
            assetsWatcher = CreateWatcher(assets);
            FileSystemWatcher packagesWatcher = CreateWatcher(packages);
            watchers = new[] { assetsWatcher, packagesWatcher };
        }
        catch (Exception exception)
        {
            if (assetsWatcher != null) assetsWatcher.Dispose();
            if (failureLogged == false)
            {
                failureLogged = true;
                Debug.LogWarning("BackgroundAutoRefresh: file watcher unavailable, retrying. " + exception.Message);
            }
            return false;
        }
        failureLogged = false;
        return true;
    }

    private static FileSystemWatcher CreateWatcher(string path)
    {
        var watcher = new FileSystemWatcher();
        try
        {
            watcher.Path = path;
            watcher.IncludeSubdirectories = true;
            watcher.InternalBufferSize = WatcherBufferSize;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
        return watcher;
    }

    private static void ScheduleRetry()
    {
        nextRetryTicks = DateTime.UtcNow.Ticks + RetryTicks;
        Interlocked.Exchange(ref watcherFailed, 1);
    }

    private static void RecoverWatchers()
    {
        if (Volatile.Read(ref watcherFailed) == 0) return;
        if (DateTime.UtcNow.Ticks < nextRetryTicks) return;
        // Clear first so an error raised by the new watchers is not lost.
        Interlocked.Exchange(ref watcherFailed, 0);
        DisposeWatchers();
        if (TryCreateWatchers() == false)
        {
            ScheduleRetry();
            return;
        }
        // Changes made while no watcher was running are unknown: refresh everything once.
        MarkPending();
    }

    // Watcher callbacks run on thread-pool threads: only record state; Unity APIs are called from Tick.
    private static void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (IsIgnored(e.FullPath) == true) return;
        MarkPending();
    }

    // A rename into an ignored name (Example.cs -> .Example.cs) still removes an asset, so either side counts.
    private static void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsIgnored(e.OldFullPath) == true && IsIgnored(e.FullPath) == true) return;
        MarkPending();
    }

    // Buffer overflow drops notifications and other errors can stop the watcher:
    // schedule a full refresh and rebuild the watchers on the main thread.
    private static void OnError(object sender, ErrorEventArgs e)
    {
        Interlocked.Exchange(ref watcherFailed, 1);
        MarkPending();
    }

    private static void MarkPending()
    {
        Interlocked.Exchange(ref lastChangeTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref pending, 1);
    }

    // Unity writes .meta files itself and skips folders ending in "~" or starting with ".".
    private static bool IsIgnored(string path)
    {
        if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (path.IndexOf("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal) >= 0) return true;
        if (path.IndexOf(Path.DirectorySeparatorChar + ".", StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    private static void Tick()
    {
        RecoverWatchers();
        if (Volatile.Read(ref pending) == 0) return;
        if (InternalEditorUtility.isApplicationActive == true)
        {
            Interlocked.Exchange(ref pending, 0);
            return;
        }
        long elapsed = DateTime.UtcNow.Ticks - Interlocked.Read(ref lastChangeTicks);
        if (elapsed < QuietTicks) return;
        if (EditorApplication.isCompiling == true || EditorApplication.isUpdating == true) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode == true) return;
        Interlocked.Exchange(ref pending, 0);
        // Respect Preferences > Asset Pipeline > Auto Refresh = Disabled.
        if (IsAutoRefreshEnabled() == false) return;
        AssetDatabase.Refresh();
    }

    // Same lookup as Unity's AssetPipelinePreferences: a missing kAutoRefreshMode falls back to the
    // legacy kAutoRefresh bool (0 = Disabled, 1 = Enabled, 2 = Enabled Outside Playmode).
    private static bool IsAutoRefreshEnabled()
    {
        bool legacyEnabled = EditorPrefs.GetBool("kAutoRefresh");
        int legacyMode = legacyEnabled == true ? 1 : 0;
        int mode = EditorPrefs.GetInt("kAutoRefreshMode", legacyMode);
        return mode != 0;
    }

    private static void DisposeWatchers()
    {
        if (watchers == null) return;
        foreach (FileSystemWatcher watcher in watchers) watcher.Dispose();
        watchers = null;
    }

    private static void Shutdown()
    {
        EditorApplication.update -= Tick;
        DisposeWatchers();
    }
}
