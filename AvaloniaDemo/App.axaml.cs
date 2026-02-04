using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AvaloniaDemo;

public class App : Application
{
    private AppContainer? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = new AppContainer();

            //var mainView = _services.GetRequiredService<MainView>();
            //desktop.MainWindow = new MainWindow { Content = mainView };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
