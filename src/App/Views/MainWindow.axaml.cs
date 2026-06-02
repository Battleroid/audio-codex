using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
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
                vm.PickFolderAsync = PickFolderAsync;
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

    protected override void OnClosed(System.EventArgs e)
    {
        Services.AppState.Instance.FlushCache();
        base.OnClosed(e);
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
