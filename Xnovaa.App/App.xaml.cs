using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xnovaa.App.Services;
using Xnovaa.App.ViewModels;
using Xnovaa.App.Views;

namespace Xnovaa.App;

public partial class App : System.Windows.Application
{
    public static IVideoLibraryService LibraryService { get; private set; } = null!;
    public static IPlaylistService PlaylistService { get; private set; } = null!;
    public static IPlaybackService PlaybackService { get; private set; } = null!;
    public static IAppStateService AppStateService { get; private set; } = null!;
    public static IStorageInfoService StorageService { get; private set; } = null!;
    public static IThumbnailService ThumbnailService { get; private set; } = null!;

    private MainViewModel? _vm;
    private static readonly object _logGate = new();

    public App()
    {
        DispatcherUnhandledException += (_, ev) =>
        {
            LogCrash("Dispatcher", ev.Exception);
            ev.Handled = true;   // keep the app alive on managed UI-thread faults
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            LogCrash("AppDomain", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            LogCrash("TaskScheduler", ev.Exception);
            ev.SetObserved();
        };
    }

    // ------------------------------------------------ diagnostics (P5)

    /// <summary>Thread-safe diagnostic line to %AppData%\Xnovaa\error.log. Never throws.</summary>
    internal static void LogDiagSafe(string msg)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataFolder, "error.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}";
            lock (_logGate)
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                File.AppendAllText(path, line);
            }
        }
        catch { /* logging must never throw */ }
    }

    internal static void LogCrash(string source, Exception? ex)
    {
        if (ex is null) return;
        var sb = new StringBuilder();
        sb.AppendLine($"================ CRASH [{source}] tid={Environment.CurrentManagedThreadId:X2} ================");
        Flatten(ex, sb, depth: 0);
        LogDiagSafe(sb.ToString());
    }

    private static void Flatten(Exception ex, StringBuilder sb, int depth)
    {
        if (depth > 5) return;
        sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] {ex.GetType().FullName}: {ex.Message}");
        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
            sb.AppendLine(ex.StackTrace);
        if (ex is AggregateException ag)
        {
            foreach (var inner in ag.InnerExceptions) Flatten(inner, sb, depth + 1);
        }
        else if (ex.InnerException is { } ie)
        {
            sb.AppendLine("---- inner ----");
            Flatten(ie, sb, depth + 1);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureFolders();
        LogDiagSafe($"==== app start (pid={Environment.ProcessId}, tid={Environment.CurrentManagedThreadId:X2}, " +
                    $"os={Environment.OSVersion.VersionString}, x64={Environment.Is64BitProcess}) ====");

        // Internal QA mode: `Xnovaa.App.exe --qa <file> [seconds]`
        if (e.Args.Length >= 2 && e.Args[0] == "--qa")
        {
            RunQa(e.Args[1], e.Args.Length >= 3 ? int.Parse(e.Args[2]) : 6);
            return;
        }

        // Internal QA mode: `Xnovaa.App.exe --qa-folder <dir> [seconds]`
        // Drives the REAL GUI (MainWindow + MainViewModel) through the folder-import
        // path, bypassing only the folder picker dialog. Diagnostic hook for the
        // "Add Folder crashes" investigation — not a feature.
        if (e.Args.Length >= 2 && e.Args[0] == "--qa-folder")
        {
            RunQaFolder(e.Args[1], e.Args.Length >= 3 ? int.Parse(e.Args[2]) : 8);
            return;
        }

        try
        {
            var logger = NullLogger.Instance;

            AppStateService = new AppStateService();
            AppStateService.Load();

            LibraryService = new VideoLibraryService();
            LibraryService.Load();

            PlaylistService = new PlaylistService();
            PlaylistService.Load();

            if (AppStateService.State.LastPlaylistId is { } pid)
            {
                var pl = PlaylistService.Playlists.FirstOrDefault(p => p.Id == pid);
                if (pl is not null) PlaylistService.ActivePlaylist = pl;
            }

            StorageService = new StorageInfoService();
            ThumbnailService = new ThumbnailService();

            PlaybackService = new PlaybackService();
            PlaybackService.Initialize(AppStateService.State.UseHardwareAcceleration);
            LogDiagSafe($"[Startup] hwAccel={AppStateService.State.UseHardwareAcceleration}");

            _vm = new MainViewModel(
                LibraryService, PlaylistService, PlaybackService,
                AppStateService, StorageService, ThumbnailService);
            _vm.SetLogger(logger);

            var window = new MainWindow { DataContext = _vm };
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            LogCrash("OnStartup", ex);
            MessageBox.Show($"Xnovaa failed to start:\n{ex.Message}", "Xnovaa",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Deterministic shutdown order (P3):
        //  1. stop the UI from raising new thumbnail work (MainWindow is already closed)
        //  2. stop + clean every in-flight thumbnail session  <- BEFORE LibVLC dies
        //  3. save persisted state (best effort, logged)
        //  4. dispose playback (stops player, disposes media, disposes LibVLC)
        LogDiagSafe($"[Shutdown] begin (exit={e.ApplicationExitCode}, tid={Environment.CurrentManagedThreadId:X2})");

        try
        {
            if (ThumbnailService is ThumbnailService ts) ts.Shutdown();
        }
        catch (Exception ex)
        {
            LogCrash("Shutdown.Thumbnails", ex);   // never swallow silently
        }

        try
        {
            if (_vm is not null)
            {
                _vm.OnWindowClosing();
                _vm.Dispose();
            }
        }
        catch (Exception ex)
        {
            LogCrash("Shutdown.ViewModel", ex);
        }

        try
        {
            if (LibraryService != null) LibraryService.Save();
            if (PlaylistService != null) PlaylistService.Save();
            if (AppStateService != null) AppStateService.Save();
        }
        catch (Exception ex)
        {
            LogCrash("Shutdown.Persist", ex);
        }

        try
        {
            if (PlaybackService != null) PlaybackService.Dispose();
        }
        catch (Exception ex)
        {
            LogCrash("Shutdown.Playback", ex);
        }

        LogDiagSafe("[Shutdown] complete");
        base.OnExit(e);
    }

    // ------------------------------------------------- internal QA harness

    private static string QaLogPath => Path.Combine(Path.GetTempPath(), "xnovaa_qa.log");

    private static void QaLog(string msg)
    {
        try { File.AppendAllText(QaLogPath, $"QA|{DateTime.Now:HH:mm:ss.fff}|{msg}\n"); } catch { }
    }

    private void RunQaFolder(string folder, int seconds)
    {
        try { File.Delete(QaLogPath); } catch { }
        QaLog("=== QA-folder run start ===");
        QaLog("folder=" + folder);

        // Identical startup to the normal path (services + real window), so the
        // import pipeline runs exactly as when a user clicks "Folders".
        try
        {
            AppStateService = new AppStateService();
            AppStateService.Load();
            LibraryService = new VideoLibraryService();
            LibraryService.Load();
            PlaylistService = new PlaylistService();
            PlaylistService.Load();
            if (AppStateService.State.LastPlaylistId is { } pid)
            {
                var pl = PlaylistService.Playlists.FirstOrDefault(p => p.Id == pid);
                if (pl is not null) PlaylistService.ActivePlaylist = pl;
            }
            StorageService = new StorageInfoService();
            ThumbnailService = new ThumbnailService();
            PlaybackService = new PlaybackService();
            PlaybackService.Initialize(AppStateService.State.UseHardwareAcceleration);

            _vm = new MainViewModel(
                LibraryService, PlaylistService, PlaybackService,
                AppStateService, StorageService, ThumbnailService);
            _vm.SetLogger(NullLogger.Instance);

            var window = new MainWindow { DataContext = _vm };
            MainWindow = window;
            window.Show();
            QaLog("window=shown");
        }
        catch (Exception ex)
        {
            QaLog("qa_folder_startup_error=" + ex);
            LogCrash("QaFolderStartup", ex);
            Shutdown(1);
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2500);   // let Loaded/InitializeFromState settle
                QaLog("invoking real folder-import pipeline");
                await Dispatcher.InvokeAsync(() => _ = _vm!.ImportFolderForQaAsync(folder));
                await Task.Delay(seconds * 1000);

                QaLog("library_count=" + LibraryService.Videos.Count);
                QaLog("mainlist_count=" + _vm!.MainList.Count);
                QaLog("playlist_count=" + _vm.PlaylistVideos.Count);
                QaLog("exiting");
                Dispatcher.Invoke(() => Shutdown());
            }
            catch (Exception ex)
            {
                QaLog("qa_folder_error=" + ex);
                LogCrash("QaFolder", ex);
                try { Dispatcher.Invoke(() => Shutdown(1)); } catch { }
            }
        });
    }

    private void RunQa(string file, int seconds)
    {
        try { File.Delete(QaLogPath); } catch { }
        QaLog("=== QA run start ===");

        ThumbnailService = new ThumbnailService();
        PlaybackService = new PlaybackService();
        PlaybackService.Initialize(true);
        QaLog("libvlc_init=ok");

        var path = Path.GetFullPath(file);
        var item = new Xnovaa.App.Models.VideoItem
        {
            FilePath = path,
            FileName = Path.GetFileNameWithoutExtension(path)
        };

        PlaybackService.DurationChanged += (_, ms) => QaLog("duration_ms=" + ms);
        PlaybackService.MediaStarted += (_, _) => QaLog("PLAYING");
        PlaybackService.MediaEnded += (_, _) => QaLog("ENDED");

        QaLog("playing=" + path);
        PlaybackService.Play(item);

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2500);
                var thumb = await ThumbnailService.EnsureThumbnailAsync(item);
                QaLog("thumb=" + (thumb ?? "none") + " exists=" + (thumb != null && File.Exists(thumb)));

                await Task.Delay(seconds * 1000);
                QaLog("volume_before=" + PlaybackService.GetVolume());
                PlaybackService.SetVolumePercent(37);
                QaLog("volume_after=" + PlaybackService.GetVolume());
                PlaybackService.SeekBy(TimeSpan.FromSeconds(15));
                await Task.Delay(1500);

                // Rapid stress: stop-play-switch race (test scenario 15/13)
                QaLog("stress: rapid switches");
                for (int i = 0; i < 5; i++)
                {
                    PlaybackService.Play(item);
                    await Task.Delay(120);
                    PlaybackService.Stop();
                    await Task.Delay(80);
                }
                var thumb2 = await ThumbnailService.EnsureThumbnailAsync(item);
                QaLog("thumb_after_stress=" + (thumb2 != null));

                QaLog("exiting");
                Dispatcher.Invoke(() => Shutdown());
            }
            catch (Exception ex)
            {
                QaLog("qa_error=" + ex);
                try { Dispatcher.Invoke(() => Shutdown(1)); } catch { }
            }
        });
    }
}
