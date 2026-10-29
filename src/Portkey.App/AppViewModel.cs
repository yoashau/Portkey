using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using Portkey.Core;
using Portkey.Infrastructure;

namespace Portkey.App;

public sealed class ConnectionViewModel : INotifyPropertyChanged
{
    public ServerProfile Profile { get; }
    public SshTunnelManager Manager { get; }
    public string Name => Profile.Name;
    public string Endpoint => (Profile.User.Length > 0 ? Profile.User + "@" : "") + Profile.Host;
    public string EndpointLabel => Endpoint.Equals(Name, StringComparison.OrdinalIgnoreCase) ? "" : Endpoint;
    public string Ports => $"本地 :{Profile.LocalPort}  →  远程 :{Profile.RemotePort}";
    public string Label => Manager.Status.Label;
    public string ToggleLabel => Manager.IsRunning ? "暂停连接" : "连接";
    public string Detail => Manager.Status.Detail;
    public string Color => Manager.Status.State == ConnectionState.Healthy ? "#28A46B" : Manager.Status.State is ConnectionState.Paused or ConnectionState.Unconfigured ? "#8B9098" : "#D9A441";
    public ConnectionViewModel(ServerProfile profile, IConnectionBackend backend, IDiagnosticLogger logger, Action changed)
    {
        Profile = profile;
        Manager = new SshTunnelManager(profile, backend, logger);
        Manager.Changed += _ => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
            changed();
        });
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AppViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly SettingsStore store;
    public DiagnosticLogger Logger { get; }
    public ProcessRunner Runner { get; } = new();
    public SshConfigReader ConfigReader { get; }
    public SshTransport Remote { get; }
    public RemoteEnvironmentInspector Inspector { get; }
    public LinuxDeploymentExecutor Executor { get; }
    private readonly OpenSshBackend backend;
    private AppSettings settings = new();
    private ConnectionViewModel? selected;
    private bool busy;
    private string message = "";
    private readonly StringBuilder recentLogs = new();
    private readonly object logSync = new();
    public ObservableCollection<ConnectionViewModel> Servers { get; } = [];
    public ConnectionViewModel? Selected { get => selected; set { selected = value; Notify(); SelectionChanged?.Invoke(); } }
    public bool Busy { get => busy; set { busy = value; Notify(); Notify(nameof(NotBusy)); } }
    public bool NotBusy => !Busy;
    public string Message { get => message; set { message = value; Notify(); } }
    public string Overall => Servers.Count == 0 ? "未配置" : Servers.All(x => x.Manager.Status.State == ConnectionState.Healthy) ? "全部正常" : Servers.All(x => x.Manager.Status.State == ConnectionState.Paused) ? "已暂停" : "需要关注";
    public event Action? StatusChanged;
    public event Action? SelectionChanged;

    public AppViewModel(SettingsStore store, DiagnosticLogger logger)
    {
        this.store = store; Logger = logger;
        ConfigReader = new SshConfigReader(Runner);
        Remote = new SshTransport(Runner);
        Inspector = new RemoteEnvironmentInspector(Remote);
        Executor = new LinuxDeploymentExecutor(Inspector, Remote);
        backend = new OpenSshBackend(Remote, logger);
        logger.Written += OnLog;
    }
    public async Task InitializeAsync()
    {
        settings = await store.LoadAsync();
        foreach (var profile in settings.Servers)
        {
            var vm = Create(profile);
            Servers.Add(vm);
            if (profile.AutoConnect) await vm.Manager.StartAsync();
        }
        Selected = Servers.FirstOrDefault();
    }
    private ConnectionViewModel Create(ServerProfile profile) => new(profile, backend, Logger, () => { Notify(nameof(Overall)); StatusChanged?.Invoke(); });
    public async Task SaveAsync(ServerProfile profile, CancellationToken ct)
    {
        profile.Validate();
        // Resolve with OpenSSH before saving; parser semantics belong to OpenSSH.
        var resolved = await ConfigReader.ResolveAsync(profile, ct);
        resolved.EnsureDedicatedTunnel();
        var next = settings.Servers.Where(x => x.Id != profile.Id).Append(profile).ToList();
        var endpoints = new List<(string Label, string Host, int RemotePort)>();
        foreach (var candidate in next)
        {
            if (candidate.Id == profile.Id)
            {
                endpoints.Add((profile.Name, resolved.HostName, profile.RemotePort));
                continue;
            }
            try
            {
                var other = await ConfigReader.ResolveAsync(candidate, ct);
                endpoints.Add((candidate.Name, other.HostName, candidate.RemotePort));
            }
            catch (IOException)
            {
                endpoints.Add((candidate.Name, candidate.Host, candidate.RemotePort));
            }
        }
        ServerProfile.EnsureDistinctEndpoints(endpoints);
        await store.SaveAsync(settings with { Servers = next }, ct);
        settings = settings with { Servers = next };
        var existing = Servers.FirstOrDefault(x => x.Profile.Id == profile.Id);
        bool wasRunning = existing?.Manager.IsRunning == true;
        if (existing is not null) { await existing.Manager.DisposeAsync(); Servers.Remove(existing); }
        var vm = Create(profile);
        Servers.Add(vm); Selected = vm;
        if (ConnectionPolicy.ShouldStartAfterSave(wasRunning, profile.AutoConnect)) await vm.Manager.StartAsync();
        Notify(nameof(Overall)); StatusChanged?.Invoke();
    }
    public async Task DeleteAsync()
    {
        if (Selected is null) return;
        var removing = Selected;
        var next = settings.Servers.Where(x => x.Id != removing.Profile.Id).ToList();
        await store.SaveAsync(settings with { Servers = next });
        settings = settings with { Servers = next };
        await removing.Manager.DisposeAsync();
        Servers.Remove(removing); Selected = Servers.FirstOrDefault();
        Notify(nameof(Overall)); StatusChanged?.Invoke();
    }
    public async Task ToggleAsync()
    {
        if (Selected is null) return;
        if (Selected.Manager.IsRunning) await Selected.Manager.PauseAsync(); else await Selected.Manager.StartAsync();
    }
    public void Wake() { foreach (var server in Servers) server.Manager.Wake(); }
    private void OnLog(string line)
    {
        lock (logSync)
        {
            if (recentLogs.Length > 30000) recentLogs.Remove(0, recentLogs.Length - 15000);
            recentLogs.AppendLine(line);
        }
    }
    public string Diagnostics()
    {
        lock (logSync)
            return $"Portkey 1.0.0 | {Environment.OSVersion} | .NET {Environment.Version}\n" + string.Join('\n', Servers.Select(x => x.Name + ": " + x.Label + " / " + x.Detail)) + "\n" + recentLogs + (Logger.StorageError is null ? "" : "\n日志写入失败：" + Logger.StorageError);
    }
    public async ValueTask DisposeAsync()
    {
        Logger.Written -= OnLog;
        await Task.WhenAll(Servers.Select(async x => await x.Manager.DisposeAsync()));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
