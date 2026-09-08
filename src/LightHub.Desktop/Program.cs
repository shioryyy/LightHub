using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LightHub.Core;
using LightHub.Application;

namespace LightHub.Desktop;

public static class Program
{
    public static bool Demo { get; private set; }
    [STAThread]
    public static int Main(string[] args)
    {
        Demo = args.Contains("--demo");
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { new TransactionStore().Log(ex); Console.Error.WriteLine(ex); return 1; }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new MainWindow(Program.Demo);
        base.OnFrameworkInitializationCompleted();
    }
}
