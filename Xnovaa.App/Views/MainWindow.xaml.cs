using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Xnovaa.App.Models;
using Xnovaa.App.Services;
using Xnovaa.App.ViewModels;

namespace Xnovaa.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private MainViewModel Vm => _vm ??= (MainViewModel)DataContext;

    private WindowStateInfo _savedState = new();
    private bool _restored;
    private bool _updatingVolume;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        KeyDown += OnGlobalKeyDown;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // LibVLC starts lazily; wire VideoView now or when the player is created.
        var existing = App.PlaybackService.GetNativePlayer();
        if (existing is not null)
        {
            VideoViewHost.MediaPlayer = existing;
        }
        else
        {
            _onPlayerCreated = p => Dispatcher.Invoke(() => VideoViewHost.MediaPlayer = p);
            App.PlaybackService.PlayerCreated += _onPlayerCreated;
        }

        RestoreWindowPlacement();
        Vm.InitializeFromState();
        Vm.FullscreenRequested += (_, _) => ToggleFullscreenInternal();
    }

    private Action<LibVLCSharp.Shared.MediaPlayer>? _onPlayerCreated;

    private void RestoreWindowPlacement()
    {
        if (_restored) return;
        _restored = true;

        var ws = App.AppStateService.State.WindowState;
        _savedState = new WindowStateInfo
        {
            X = ws.X, Y = ws.Y, Width = ws.Width, Height = ws.Height, IsMaximized = ws.IsMaximized
        };

        if (double.IsNaN(ws.X) || double.IsNaN(ws.Y))
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var screenW = SystemParameters.WorkArea.Width;
        var screenH = SystemParameters.WorkArea.Height;

        Left = Math.Min(Math.Max(ws.X, -ws.Width * 0.2), screenW - 80);
        Top = Math.Min(Math.Max(ws.Y, 0), screenH - 60);
        Width = Math.Min(Math.Max(ws.Width, MinWidth), screenW);
        Height = Math.Min(Math.Max(ws.Height, MinHeight), screenH);

        if (ws.IsMaximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_onPlayerCreated is not null)
        {
            App.PlaybackService.PlayerCreated -= _onPlayerCreated;
            _onPlayerCreated = null;
        }
        SaveWindowPlacement();
        Vm.OnWindowClosing();
        Vm.Dispose();
    }

    private void SaveWindowPlacement()
    {
        var ws = App.AppStateService.State.WindowState;
        if (WindowState == WindowState.Maximized)
        {
            ws.IsMaximized = true;
        }
        else
        {
            ws.IsMaximized = false;
            ws.X = Left;
            ws.Y = Top;
            ws.Width = ActualWidth;
            ws.Height = ActualHeight;
        }
    }

    // ------------------------------------------------- custom chrome buttons

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeGlyph()
        => MaximizeBtn.Content = WindowState == WindowState.Maximized ? "❐" : "☐";

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------- seek slider

    private void OnSeekDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        => Vm.BeginSeekDrag();

    private void OnSeekDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        => Vm.EndSeekDrag();

    // ------------------------------------------------- volume

    private void OnVolumeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolume || _vm is null) return;
        _updatingVolume = true;
        try
        {
            Vm.SetVolumeFromSliderCommand.Execute(null);
        }
        finally
        {
            _updatingVolume = false;
        }
    }

    // ------------------------------------------------- playlist interactions

    private void OnPlaylistDoubleClick(object sender, MouseButtonEventArgs e)
        => Vm.PlayItemCommand.Execute(Vm.SelectedItem);

    private void OnLibraryDoubleClick(object sender, MouseButtonEventArgs e)
        => Vm.PlayItemCommand.Execute(Vm.SelectedItem);

    private VideoItem? ItemFromButton(object sender)
    {
        if (sender is FrameworkElement fe && fe.DataContext is VideoItem v) return v;
        return Vm.SelectedItem;
    }

    private void OnItemMenuClick(object sender, RoutedEventArgs e)
    {
        var item = ItemFromButton(sender);
        if (item is null) return;

        var menu = new ContextMenu();

        var play = new MenuItem { Header = "Play now" };
        play.Click += (_, _) => Vm.PlayItemCommand.Execute(item);
        var remove = new MenuItem { Header = "Remove from playlist" };
        remove.Click += (_, _) => Vm.RemoveFromPlaylistCommand.Execute(item);
        var fav = new MenuItem { Header = item.IsFavorite ? "Unfavorite" : "Mark as Favorite" };
        fav.Click += (_, _) => Vm.ToggleFavoriteCommand.Execute(item);
        var reveal = new MenuItem { Header = "Reveal in Explorer" };
        reveal.Click += (_, _) => Vm.RevealInExplorerCommand.Execute(item);

        menu.Items.Add(play);
        menu.Items.Add(remove);
        menu.Items.Add(fav);
        menu.Items.Add(reveal);
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void OnCtxPlay(object sender, RoutedEventArgs e) => Vm.PlayItemCommand.Execute(Vm.SelectedItem);
    private void OnCtxRemove(object sender, RoutedEventArgs e) => Vm.RemoveFromPlaylistCommand.Execute(Vm.SelectedItem);
    private void OnCtxFavorite(object sender, RoutedEventArgs e) => Vm.ToggleFavoriteCommand.Execute(Vm.SelectedItem);
    private void OnCtxReveal(object sender, RoutedEventArgs e) => Vm.RevealInExplorerCommand.Execute(Vm.SelectedItem);

    private void OnAddToPlaylistClick(object sender, RoutedEventArgs e)
        => Vm.ImportFilesCommand.Execute(null);

    // ------------------------------------------------- keyboard shortcuts (Plan §4.7)

    private void OnGlobalKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // don't hijack typing in the search box

        switch (e.Key)
        {
            case Key.Space:
                Vm.TogglePlayPauseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left when Keyboard.Modifiers == ModifierKeys.Control:
                Vm.SeekStepCommand.Execute("-");
                e.Handled = true;
                break;
            case Key.Right when Keyboard.Modifiers == ModifierKeys.Control:
                Vm.SeekStepCommand.Execute("+");
                e.Handled = true;
                break;
            case Key.Left:
                Vm.SeekBySecondsCommand.Execute("-5");
                e.Handled = true;
                break;
            case Key.Right:
                Vm.SeekBySecondsCommand.Execute("5");
                e.Handled = true;
                break;
            case Key.Up:
                AdjustVolume(+5);
                e.Handled = true;
                break;
            case Key.Down:
                AdjustVolume(-5);
                e.Handled = true;
                break;
            case Key.F:
                ToggleFullscreenInternal();
                e.Handled = true;
                break;
            case Key.Delete:
                Vm.RemoveSelectedFromPlaylistCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void AdjustVolume(int delta)
    {
        Vm.Volume = Math.Clamp(Vm.Volume + delta / 100.0, 0, 1);
        Vm.SetVolumeFromSliderCommand.Execute(null);
    }

    // ------------------------------------------------- fullscreen

    private void ToggleFullscreenInternal()
    {
        if (WindowState == WindowState.Maximized && WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            ApplySavedBounds();
            Vm.IsFullscreen = false;
        }
        else
        {
            SaveWindowPlacement();
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Vm.IsFullscreen = true;
        }
    }

    private void ApplySavedBounds()
    {
        var ws = _savedState;
        if (!double.IsNaN(ws.X)) Left = ws.X;
        if (!double.IsNaN(ws.Y)) Top = ws.Y;
        Width = ws.Width;
        Height = ws.Height;
    }
}
