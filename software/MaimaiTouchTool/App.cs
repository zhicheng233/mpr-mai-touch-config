using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MaimaiTouchTool.Localization;

namespace MaimaiTouchTool;

public sealed class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Resources.MergedDictionaries.Add(Localizer.Instance.Resources);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var forced = Environment.GetEnvironmentVariable("MAIMAI_LANG");
        if (!string.IsNullOrEmpty(forced)) Localizer.Instance.SetLanguage(forced);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
