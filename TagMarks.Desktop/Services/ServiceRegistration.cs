using System;
using System.Runtime.InteropServices;
using TagMarks.Desktop.ViewModels;
using Cauldron.Models;
using Cauldron.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TagMarks.Desktop.Services;

internal static class ServiceRegistration
{
    public static IServiceProvider ConfigureServices()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder
            .AddConfiguration(configuration.GetSection("Logging"))
            .AddConsole());
        services.AddSingleton<Cauldron.Managers.IConfigurationManager>(sp => {
            var cm = new Cauldron.Managers.ConfigurationManager(
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("ConfigurationManager"));
            cm.InitializeConfiguration();
            return cm;
        });
        services.AddSingleton<ITagMarkSerializer, BatTagMarkSerializer>();
        services.AddSingleton<IUriLauncher, ProcessUriLauncher>();
        services.AddSingleton<ITagMarkUriService, TagMarkUriService>();
        services.AddSingleton<IClipboardService>(new SynchronizedClipboardService(new ClipboardService()));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IBackupService, BackupService>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            services.AddSingleton<IWindowFocusService, Win32WindowFocusService>();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            services.AddSingleton<IWindowFocusService, MacWindowFocusService>();
        else
            services.AddSingleton<IWindowFocusService, DefaultWindowFocusService>();
        services.AddSingleton(sp => GlobalHotkeySetting.Resolve(
            sp.GetRequiredService<Cauldron.Managers.IConfigurationManager>().Config.GlobalHotkey,
            HostPlatforms.Current,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(GlobalHotkeySetting))));
        services.AddSingleton(sp => ShortcutCatalog.For(
            PlatformShortcutMap.Current.IsMac, sp.GetRequiredService<ResolvedGlobalHotkey>().Hotkey));
        services.AddSingleton<IGlobalHotkeyService>(sp => {
            var config = sp.GetRequiredService<Cauldron.Managers.IConfigurationManager>().Config;
            return new GlobalHotkeyService(sp.GetRequiredService<ILogger<GlobalHotkeyService>>(),
                sp.GetRequiredService<ResolvedGlobalHotkey>().Hotkey, config.DoubleTapMs);
        });
        services.AddTransient<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
