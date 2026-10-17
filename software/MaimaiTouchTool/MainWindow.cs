using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using System.ComponentModel;
using System.Diagnostics;
using MaimaiTouchTool.ViewModels;

namespace MaimaiTouchTool;

public sealed partial class MainWindow : Window
{
    private bool closing;
    private readonly MainViewModel viewModel;

    public int TouchBoardChildCount => TouchBoardCanvas.Children.Count;

    public MainWindow()
    {
        InitializeComponent();
        viewModel = new MainViewModel();
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        BuildTouchBoard(viewModel.TouchKeys);
        Closing += OnClosing;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.IsToastVisible)) return;
        var toast = this.FindControl<Border>("ToastOverlay");
        if (toast is null) return;
        toast.RenderTransform = new TranslateTransform(0, viewModel.IsToastVisible ? 24 : 0);
        if (viewModel.IsToastVisible)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => toast.RenderTransform = new TranslateTransform(0, 0));
    }

    // Shift+滚轮在"步进"行上调整单键微调步进（普通滚轮仍交给页面滚动）。
    private void OnStepWheel(object? sender, PointerWheelEventArgs args)
    {
        if (!args.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        viewModel.Editor.CycleStep(args.Delta.Y > 0 ? 1 : -1);
        args.Handled = true;
    }

    private async void OnCopyConfig(object? sender, RoutedEventArgs args) => await CopyAsync(viewModel.McpGuide.ConfigSnippet);

    private async void OnCopyTest(object? sender, RoutedEventArgs args) => await CopyAsync(viewModel.McpGuide.TestCommand);

    private async Task CopyAsync(string text)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            await clipboard.SetTextAsync(text);
            viewModel.McpGuide.Copied = true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Clipboard copy failed: {ex.Message}");
        }
    }

    private void BuildTouchBoard(IEnumerable<TouchKeyModel> keys)
    {
        foreach (var key in keys)
        {
            Control image;
            Image? svg = null;
            try
            {
                svg = new Image
                {
                    Width = key.Position.Width,
                    Height = key.Position.Height,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Stretch = Stretch.Uniform
                };
                UpdateImage(svg, key.ImagePath);
                image = svg;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Touch SVG control creation failed for {key.ImagePath}: {ex.Message}");
                throw;
            }
            var label = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 28,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                Text = key.Label
            };
            var content = new Grid();
            content.Children.Add(image);
            content.Children.Add(label);

            var item = new Grid
            {
                Width = key.Position.Width,
                Height = key.Position.Height
            };
            item.Children.Add(content);
            Canvas.SetLeft(item, key.Position.X);
            Canvas.SetTop(item, key.Position.Y);
            TouchBoardCanvas.Children.Add(item);
            key.PropertyChanged += (_, args) =>
            {
                if ((args.PropertyName is null or nameof(TouchKeyModel.ImagePath)) && image is Image currentImage)
                {
                    try { UpdateImage(currentImage, key.ImagePath); }
                    catch (Exception ex) { Trace.WriteLine($"Touch SVG reload failed for {key.ImagePath}: {ex.Message}"); }
                }
            };
        }
    }

    private static void UpdateImage(Image image, string path)
    {
        try
        {
             var source = SvgSource.Load(path, baseUri: null) ?? throw new InvalidOperationException($"SVG source was empty: {path}");
             image.Source = new SvgImage { Source = source };
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Touch SVG load failed for {path}: {ex.Message}");
            throw;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        closing = true;
        if (DataContext is IAsyncDisposable disposable) await disposable.DisposeAsync();
        Close();
    }
}
