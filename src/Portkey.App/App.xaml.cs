using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;
using Portkey.Infrastructure;

namespace Portkey.App;

public partial class App : Application
{
    private SingleInstance? instance;
    private EventWaitHandle? activate;
    private RegisteredWaitHandle? activationWait;
    private TrayManager? tray;
    private TrayPanel? panel;
    private AppViewModel? model;
    private DiagnosticLogger? logger;
    private bool stopping;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string user = WindowsIdentity.GetCurrent().User!.Value;
        instance = new SingleInstance(@"Local\Portkey.SingleInstance." + user);
        activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Portkey.Activate." + user);
        if (!instance.IsFirst) { activate.Set(); Shutdown(); return; }
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Portkey");
        logger = new DiagnosticLogger(Path.Combine(directory, "Logs"));
        try
        {
            model = new AppViewModel(new SettingsStore(directory), logger);
            await model.InitializeAsync();
            panel = new TrayPanel(model);
            tray = new TrayManager(panel, model, ExitAsync);
            activationWait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => Dispatcher.BeginInvoke(() =>
            {
                logger.Write("app", "Activation request received");
                tray.Show();
            }), null, Timeout.Infinite, false);
            NetworkChange.NetworkAvailabilityChanged += NetworkAvailable;
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.UserPreferenceChanged += ThemeChanged;
            SessionEnding += OnSessionEnding;
            logger.Write("app", $"Portkey started; tray only; pid={Environment.ProcessId}; panelVisible={panel.IsVisible}");
            if (e.Args.Contains("--show")) tray.Show();
        }
        catch (Exception error)
        {
            logger.Write("startup", error.Message);
            MessageBox.Show("Portkey 无法启动：\n" + error.Message + "\n\n配置文件未被重置。", "Portkey", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
        }
    }

    private void NetworkAvailable(object? sender, NetworkAvailabilityEventArgs e) { if (e.IsAvailable) model?.Wake(); }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) model?.Wake(); }
    private void ThemeChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => tray?.Refresh());
    private async void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) => await ExitAsync();
    public async Task ExitAsync()
    {
        if (stopping) return;
        stopping = true;
        activationWait?.Unregister(null);
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailable;
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.UserPreferenceChanged -= ThemeChanged;
        if (panel is not null) { panel.CancelWork(); panel.Hide(); }
        if (model is not null) await model.DisposeAsync();
        tray?.Dispose();
        if (logger is not null) await logger.DisposeAsync();
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        activate?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
