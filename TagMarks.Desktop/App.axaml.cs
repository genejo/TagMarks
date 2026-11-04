using System;
using System.IO;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TagMarks.Desktop.Managers;
using TagMarks.Desktop.Services;
using TagMarks.Desktop.ViewModels;
using TagMarks.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Cauldron.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI.Binding.Reactive;
using ReactiveUI.Reactive;
using Splat;

namespace TagMarks.Desktop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    
    private TrayIcon? _trayIcon;
    
    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            Services = ServiceRegistration.ConfigureServices();
            
            // ReactiveUI resolves IViewLocator from Splat, not from MS.DI.
            Locator.CurrentMutable.RegisterLazySingleton(() => new ViewLocator(), typeof(IViewLocator));
            
            var appLogger = Services.GetRequiredService<ILogger<App>>();
            SingleInstanceManager.SetLogger(Services.GetRequiredService<ILoggerFactory>());
            
            var configManager = Services.GetRequiredService<Cauldron.Managers.IConfigurationManager>();
            appLogger.LogInformation("TagMarks folder path: {Path}", configManager.Config.TagMarksFolderPath);

            // Without its folder TagMarks can't run (spec §11 #53): say why and exit when the dialog closes,
            // before anything that reads the folder is resolved.
            if (configManager.TagMarksFolderError is { } folderError) {
                var errorWindow = new StartupErrorWindow(folderError);
                errorWindow.Closed += (_, _) => { SingleInstanceManager.Cleanup(); desktop.Shutdown(1); };
                desktop.MainWindow = errorWindow;
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var mainWindowViewModel = Services.GetRequiredService<MainWindowViewModel>();
            MainWindowView mainWindowView = new MainWindowView {
                DataContext = mainWindowViewModel
            };

            var focusService = Services.GetRequiredService<IWindowFocusService>();
            // Local helper: delegates to the platform service, then notifies the view.
            void BringWindowToForeground() {
                focusService.BringToForeground(mainWindowView);
                mainWindowView.OnComingToForeground();
            }

            desktop.MainWindow = mainWindowView;
            
            // Route second-instance activation through the same path as hotkey/tray.
            SingleInstanceManager.SetActivationCallback(BringWindowToForeground);
            
            var hotkeyService = Services.GetRequiredService<IGlobalHotkeyService>();

            // Daily backups (spec §10.5): checked a minute after startup, then hourly, off the UI thread.
            var backupService = Services.GetRequiredService<IBackupService>();
            var backupTimer = Observable
                .Timer(TimeSpan.FromMinutes(1), TimeSpan.FromHours(1), RxSchedulers.TaskpoolScheduler)
                .Subscribe(_ => backupService.RunIfDue());

            desktop.Exit += (_, _) => { mainWindowViewModel.Dispose(); hotkeyService.Dispose(); backupTimer.Dispose(); _trayIcon?.Dispose(); SingleInstanceManager.Cleanup(); };
            
            // Subscribe to the global hotkey activation signal.
            hotkeyService.Activated.Subscribe(_ => BringWindowToForeground());
            
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "avalonia-logo.ico");
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(iconPath),
                ToolTipText = "TagMark",
                Menu = new NativeMenu(),
                IsVisible = true
            };

            var showItem = new NativeMenuItem("Show");
            showItem.Click += (_, _) => BringWindowToForeground();

            // Opens the window on the shortcut help panel, starting from plain SEARCH.
            var shortcutsItem = new NativeMenuItem("Keyboard Shortcuts");
            shortcutsItem.Click += (_, _) => {
                BringWindowToForeground();
                mainWindowViewModel.ResetToSearchState();
                mainWindowViewModel.ShowHelpPanel();
            };

            var exitItem = new NativeMenuItem("Exit");
            exitItem.Click += (_, _) => desktop.Shutdown();

            _trayIcon.Menu.Items.Add(showItem);
            _trayIcon.Menu.Items.Add(shortcutsItem);
            _trayIcon.Menu.Items.Add(exitItem);
            
            _trayIcon.Clicked += (_, _) => BringWindowToForeground();

            // A GlobalHotkey setting that doesn't parse falls back to the default; say so on the next show.
            if (Services.GetRequiredService<ResolvedGlobalHotkey>().IsSettingInvalid)
                mainWindowViewModel.ReportInvalidHotkeySetting();

            // A config file that can't be parsed is ignored for this run (spec §11 #55); say so on the next show.
            if (configManager.IsConfigFileUnreadable)
                mainWindowViewModel.ReportUnreadableConfigFile(configManager.ConfigFilePath);

            // Without the hook the tray icon is the only way in: say so on the icon and on the next show.
            hotkeyService.Failed.Subscribe(_ => {
                _trayIcon.ToolTipText = "TagMark (hotkey unavailable)";
                mainWindowViewModel.ReportHotkeyUnavailable();
            });

            // Since Avalonia 12 a TrayIcon creates its native icon only once it's attached to the
            // application's TrayIcons; a standalone instance never shows up.
            TrayIcon.SetIcons(this, [_trayIcon]);
        }

        base.OnFrameworkInitializationCompleted();
    }
}