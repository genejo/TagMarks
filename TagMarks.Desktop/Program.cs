using TagMarks.Desktop.Managers;
using Avalonia;
using ReactiveUI.Avalonia.Reactive;
using System;
using System.IO;
using System.IO.Pipes;

namespace TagMarks.Desktop;

internal class Program {
    private const string UniqueAppId = "gene.tagmark";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) {
        if (!SingleInstanceManager.EnsureSingleInstance(UniqueAppId))
        {
            // Tell existing instance to activate
            using var client = new NamedPipeClientStream(".", UniqueAppId, PipeDirection.Out);
            client.Connect(100);
            using var writer = new StreamWriter(client);
            writer.WriteLine("activate");
            writer.Flush();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            // A tray-only (accessory) app on macOS: no Dock icon, and its window can appear
            // over another app's fullscreen Space.
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .UseReactiveUI(_ => { });
}