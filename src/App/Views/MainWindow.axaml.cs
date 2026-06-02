using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MarathonAudio.App.Controls;
using MarathonAudio.App.ViewModels;

namespace MarathonAudio.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PickFolderAsync = PickFolderAsync;
                vm.ConfirmAsync = ConfirmAsync;
            }
        };
        // Intercept Space before children so it toggles playback (unless typing in a text box).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is TextBox) return;            // let space type in the search box
        Vm?.PlayPauseFromKey();
        e.Handled = true;
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private async void OnCopyText(object? sender, TappedEventArgs e)
    {
        if (sender is not TextBlock tb || string.IsNullOrEmpty(tb.Text)) return;
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip == null) return;
        await clip.SetTextAsync(tb.Text);
        if (Vm is { } vm) vm.StatusText = $"Copied to clipboard: {tb.Text}";
    }

    protected override void OnClosed(System.EventArgs e)
    {
        // Dispose the view model so an in-flight transcribe run is cancelled (killing its
        // whisper-cli children) and both caches are flushed. Fall back to a direct flush if
        // the DataContext isn't our VM for some reason.
        if (Vm is { } vm) vm.Dispose();
        else
        {
            Services.AppState.Instance.FlushCache();
            Services.AppState.Instance.FlushTranscripts();
        }
        base.OnClosed(e);
    }

    /// <summary>Minimal modal yes/no confirmation (no XAML); returns true if confirmed.</summary>
    private async Task<bool> ConfirmAsync(string message)
    {
        var yes = new Button
        {
            Content = "Transcribe", Width = 120, Background = Avalonia.Media.Brush.Parse("#c8f000"),
            Foreground = Avalonia.Media.Brush.Parse("#0b0b0c"), BorderThickness = new Avalonia.Thickness(0),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        var no = new Button { Content = "Cancel", Width = 100 };
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);

        var panel = new StackPanel { Margin = new Avalonia.Thickness(22), Spacing = 18 };
        panel.Children.Add(new TextBlock
        {
            Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brush.Parse("#e8e8e6"), FontSize = 13,
        });
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = "Confirm", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            Background = Avalonia.Media.Brush.Parse("#16161a"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel,
        };
        yes.Click += (_, _) => dlg.Close(true);
        no.Click += (_, _) => dlg.Close(false);
        return await dlg.ShowDialog<bool>(this);
    }

    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder",
            AllowMultiple = false,
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        var result = await new SettingsWindow().ShowDialog<SettingsResult?>(this);
        if (Vm is not { } vm) return;
        vm.RefreshGameDir();
        vm.UpdateExportTarget();
        if (result == null) return;
        if (result.GameDirChanged && vm.IndexLoaded)
            vm.StatusText = "Game folder changed — click “Load sounds” to re-index.";
        if (result.WordlistChanged && vm.IndexLoaded)
            await vm.ApplyWordlistAsync();
        if (result.ParakeetSelected)
            await vm.EnsureParakeetAsync();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && Vm is { } vm)
            vm.SetSelection(lb.SelectedItems?.Cast<object?>() ?? System.Array.Empty<object?>());
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        Vm?.RequestPlaySelected();
    }

    // Lazy-load a row's preview (duration + mini waveform) when it scrolls into view.
    private void OnRowAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control c && c.DataContext is SoundRow row)
            Vm?.EnsureRowLoaded(row);
    }

    private void OnWaveformPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not WaveformControl wf || Vm is not { } vm) return;
        double x = e.GetPosition(wf).X;
        if (wf.Bounds.Width > 0)
            vm.SeekToFraction(x / wf.Bounds.Width);
    }
}
