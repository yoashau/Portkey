using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
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
    // The selector already shows the name. Do not repeat user@host when the host is that name.
    public string EndpointLabel => Profile.Host.Equals(Name, StringComparison.OrdinalIgnoreCase) || Endpoint.Equals(Name, StringComparison.OrdinalIgnoreCase) ? "" : Endpoint;
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
    public string Message { get => message; private set { message = value; Notify(); Notify(nameof(NoticeText)); Notify(nameof(NoticeDetail)); Notify(nameof(NoticeTone)); } }
    private string messageTone = "";
    private int messageVersion;
    private CancellationTokenSource? messageDismiss;
    public string NoticeDetail => message.Length > 0 ? message : SyncWarning;
    public string NoticeText => Summarize(NoticeDetail);
    public string NoticeTone => message.Length > 0 ? messageTone : SyncWarning.Length == 0 ? "" : "warning";
    public void ShowMessage(string? text, bool transient = true, Action? onDismiss = null)
    {
        messageDismiss?.Cancel();
        messageDismiss?.Dispose();
        messageDismiss = null;
        messageTone = ClassifyNotice(text ?? "");
        Message = text ?? "";
        int version = ++messageVersion;
        if (!transient || Message.Length == 0) return;
        var dismiss = new CancellationTokenSource();
        messageDismiss = dismiss;
        _ = DismissMessageAsync(version, MessageLifetime(Message), dismiss.Token, onDismiss);
    }
    public void ClearMessage()
    {
        messageDismiss?.Cancel();
        messageDismiss?.Dispose();
        messageDismiss = null;
        messageVersion++;
        messageTone = "";
        Message = "";
    }
    private static string ClassifyNotice(string text)
    {
        if (text.Contains("没有同步", StringComparison.Ordinal) || text.Contains("未能", StringComparison.Ordinal) || text.Contains("失败", StringComparison.Ordinal) || text.Contains("未完成", StringComparison.Ordinal) || text.Contains("超时", StringComparison.Ordinal) || text.Contains("冲突", StringComparison.Ordinal) || text.Contains("取消", StringComparison.Ordinal))
            return "warning";
        return "info";
    }
    private static string Summarize(string text)
    {
        if (text.Length == 0) return "";
        if (text.StartsWith("没有同步", StringComparison.Ordinal)) return "没有同步，新终端仍使用旧地址。";
        if (text.Length <= 24) return text;
        int end = text.IndexOf('。');
        return end is >= 4 and <= 24 ? text[..(end + 1)] : text;
    }
    private static TimeSpan MessageLifetime(string text) => TimeSpan.FromSeconds(Math.Min(12, 5 + text.Length / 18.0));
    private async Task DismissMessageAsync(int version, TimeSpan delay, CancellationToken cancellationToken, Action? onDismiss)
    {
        try { await Task.Delay(delay, cancellationToken); }
        catch (OperationCanceledException) { return; }
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (version != messageVersion) return;
            Message = "";
            onDismiss?.Invoke();
        });
    }
    private string syncWarning = "";
    public string SyncWarning { get => syncWarning; private set { syncWarning = value; Notify(); Notify(nameof(NoticeText)); Notify(nameof(NoticeDetail)); Notify(nameof(NoticeTone)); } }
    internal void SetSyncWarning(string text) => SyncWarning = text;
    public string SyncFollowUp { get; private set; } = "";
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
    public async Task<string> SaveAsync(ServerProfile profile, bool replaceEdited, CancellationToken ct)
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
        string synced;
        string followUp = "";
        try { synced = await SyncRemoteAsync(profile, replaceEdited, ct); }
        catch (Exception error)
        {
            Logger.Write("sync", error.Message);
            followUp = RemoteEnvironmentSync.FollowUp(error.Message);
            synced = RemoteEnvironmentSync.NotSynced(error.Message);
        }
        if (ConnectionPolicy.ShouldStartAfterSave(wasRunning, profile.AutoConnect)) await vm.Manager.StartAsync();
        SyncWarning = synced.StartsWith("没有同步", StringComparison.Ordinal) ? synced : "";
        SyncFollowUp = SyncWarning.Length == 0 ? "" : followUp;
        Notify(nameof(SyncWarning));
        Notify(nameof(SyncFollowUp));
        Notify(nameof(Overall)); StatusChanged?.Invoke();
        return synced;
    }
    public async Task<string> DeleteAsync(CancellationToken ct = default)
    {
        if (Selected is null) return "没有可删除的连接。";
        var removing = Selected;
        string remote;
        try { remote = await RemoveRemoteAsync(removing.Profile, ct); }
        catch (Exception error)
        {
            Logger.Write("sync", error.Message);
            remote = "服务器上的配置未能清除：" + error.Message;
        }
        var next = settings.Servers.Where(x => x.Id != removing.Profile.Id).ToList();
        await store.SaveAsync(settings with { Servers = next });
        settings = settings with { Servers = next };
        await removing.Manager.DisposeAsync();
        Servers.Remove(removing); Selected = Servers.FirstOrDefault();
        Notify(nameof(Overall)); StatusChanged?.Invoke();
        return "已删除本机连接。" + remote;
    }
    public async Task<string> RecoverRemoteAsync(ServerProfile profile, CancellationToken ct)
    {
        string result = await Inspector.RecoverAsync(profile, ct);
        SyncFollowUp = "";
        SyncWarning = "中断的写入已恢复。请再保存一次，服务器配置才会和当前设置一致。";
        Notify(nameof(SyncFollowUp));
        Notify(nameof(SyncWarning));
        return SyncWarning + result;
    }
    public async Task<string> RestoreLastRemoteAsync(ServerProfile profile, CancellationToken ct)
    {
        var snapshot = await Inspector.InspectAsync(profile, ct);
        var backup = snapshot.Backups.FirstOrDefault() ?? throw new InvalidOperationException("没有可撤销的服务器写入。");
        foreach (var change in backup.Restore)
            if (snapshot.Files[change.Path].Hash != change.BeforeHash) throw new InvalidOperationException("这些文件在备份后又被改过，没有撤销。");
        var plan = new DeploymentPlan("恢复备份 " + backup.Id, backup.Restore, ["恢复最近一次提交前的文件。"], snapshot.Systemd);
        Logger.Write("sync", await Executor.ApplyAsync(profile, snapshot, plan, ct));
        return "已把服务器文件退回上一次写入之前。本机连接设置没有改。";
    }
    private async Task<string> SyncRemoteAsync(ServerProfile profile, bool replaceEdited, CancellationToken ct)
    {
        var snapshot = await Inspector.InspectAsync(profile, ct);
        var plan = RemoteEnvironmentSync.Plan(snapshot, profile, replaceEdited);
        if (plan.Changes.Count == 0) return RemoteEnvironmentSync.Saved(profile, false, null);
        string report = await Executor.ApplyAsync(profile, snapshot, plan, ct);
        Logger.Write("sync", report);
        return RemoteEnvironmentSync.Saved(profile, true, report);
    }
    private async Task<string> RemoveRemoteAsync(ServerProfile profile, CancellationToken ct)
    {
        var snapshot = await Inspector.InspectAsync(profile, ct);
        var plan = LinuxDeploymentPlanner.Create(snapshot, profile, uninstall: true, allowEdited: true);
        if (plan.Changes.Count == 0) return "服务器上没有 Portkey 写入的代理配置。";
        Logger.Write("sync", await Executor.ApplyAsync(profile, snapshot, plan, ct));
        return "已清除服务器上由 Portkey 写入的代理配置。";
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
