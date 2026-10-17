using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using RunbookWizard.App.ViewModels;

namespace RunbookWizard.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PickGuide = PickGuideAsync;
                vm.Settings.CopyText = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
            }
        };

        // The window is up: a freshly installed update may drop its rollback backup.
        Opened += (_, _) => TomLabs.AutoUpdate.Updater.Current?.MarkHealthy();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        PaletteList.PointerReleased += OnPaletteItemPointerReleased;
        OutputScroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y > 0)
                OutputScroll.ScrollToEnd();
        };
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private async Task<string?> PickGuideAsync()
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Open a guide",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Runbook guides") { Patterns = new[] { "*.runbook.md", "*.runbook.json", "*.md", "*.json" } },
                FilePickerFileTypes.All,
            },
        };
        var file = (await StorageProvider.OpenFilePickerAsync(options)).FirstOrDefault();
        return file?.TryGetLocalPath();
    }

    // ----- keyboard, palette, title bar -----

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
            return;

        if (vm.Palette.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    vm.Palette.Close();
                    e.Handled = true;
                    break;
                case Key.Down:
                    vm.Palette.MoveSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    vm.Palette.MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    vm.Palette.RunSelected();
                    e.Handled = true;
                    break;
            }
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.K)
        {
            OpenPalette();
            e.Handled = true;
        }
    }

    private void OpenPalette()
    {
        if (Vm is not { } vm)
            return;
        vm.Palette.Open();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PaletteBox.Focus();
            PaletteBox.SelectAll();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private void OnPaletteButtonClick(object? sender, RoutedEventArgs e) => OpenPalette();

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e) => Vm?.Palette.Close();

    private void OnPaletteItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(true) != null)
            Vm?.Palette.RunSelected();
    }

    /// <summary>The header doubles as the title bar: drag moves the window, double-click toggles maximize.</summary>
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(true) != null)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            BeginMoveDrag(e);
    }
}
