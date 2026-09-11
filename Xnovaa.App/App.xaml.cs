using System;
using System.IO;
using System.Linq;
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

    public App()
    {
        DispatcherUnhandledException += (_, ev) =>
        {
            LogCrash("Dispatcher", ev.Exception);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            LogCrash("AppDomain", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            LogCrash("TaskScheduler", ev.Exception);
            ev.SetObserved();
        };
    }

    internal static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataFolder, "error.log");
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.AppendAllText(path, $"[{DateTime.Now:O}] {source}: {ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureFolders();

        // Internal QA mode: `Xnovaa.App.exe --qa <file> [seconds]`
        // Plays a file headlessly, logs position/duration/thumbnail results, then exits.
        if (e.Args.Length >= 2 && e.Args[0] == "--qa")
        {
            RunQa(e.Args[1], e.Args.Length >= 3 ? int.Parse(e.Args[2]) : 6);
            return;
        }

        // Lightweight logging (Plan §3: optional). No console/file sink overhead in Release.
        var logger = NullLogger.Instance;

        AppStateService = new AppStateService();
        AppStateService.Load();

        LibraryService = new VideoLibraryService();
        LibraryService.Load();

        PlaylistService = new PlaylistService();
        PlaylistService.Load();

        // Restore last active playlist if it still exists.
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
        _vm.SetLogger(logger);

        var window = new MainWindow { DataContext = _vm };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (LibraryService != null) LibraryService.Save();
            if (PlaylistService != null) PlaylistService.Save();
        }
        catch { /* best effort on exit */ }

        PlaybackService.Dispose();
        base.OnExit(e);
    }

    // ------------------------------------------------- internal QA harness

    private static string QaLogPath => Path.Combine(Path.GetTempPath(), "xnovaa_qa.log");

    private static void QaLog(string msg)
    {
        try { File.AppendAllText(QaLogPath, $"QA|{DateTime.Now:HH:mm:ss.fff}|{msg}\n"); } catch { }
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
        PlaybackService.TimeChanged += (_, ms) => QaLog("time_ms=" + ms);
        PlaybackService.MediaStarted += (_, _) => QaLog("PLAYING");
        PlaybackService.MediaEnded += (_, _) => QaLog("ENDED");

        QaLog("playing=" + path);
        PlaybackService.Play(item);

        Task.Run(async () =>
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
            QaLog("exiting");
            Dispatcher.Invoke(() => Shutdown());
        });
    }
}
