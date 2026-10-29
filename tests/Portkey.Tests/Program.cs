using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Portkey.Core;
using Portkey.Infrastructure;

if (args is ["--live-readonly", var liveHost, var livePort, var reportDirectory])
    return await LiveTests.RunAsync(liveHost, int.Parse(livePort), reportDirectory);

if (args is ["--live-deploy", var deployHost, var deployPort, var deployReport])
    return await LiveDeploymentTests.RunAsync(deployHost, int.Parse(deployPort), deployReport);

if (args is ["--child-hold", var marker])
{
    await File.WriteAllTextAsync(marker, Environment.ProcessId.ToString());
    await Task.Delay(TimeSpan.FromSeconds(30));
    return 0;
}
if (args is ["--hold-mutex", var mutexName, var mutexMarker])
{
    using var gate = new SingleInstance(mutexName);
    await File.WriteAllTextAsync(mutexMarker, gate.IsFirst ? "first" : "second");
    await Task.Delay(Timeout.Infinite);
    return 0;
}

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> run) => tests.Add((name, run));
void Assert(bool value, string detail = "Assertion failed") { if (!value) throw new Exception(detail); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
RemoteSnapshot Fresh(bool systemd = true) => new()
{
    Uid = 1000, User = "tester", Home = "/home/tester", Shell = "/bin/bash", Os = "ID=ubuntu\n", Systemd = systemd,
    Files = LinuxDeploymentPlanner.ShellFiles.Concat(new[] { LinuxDeploymentPlanner.ShellPath, LinuxDeploymentPlanner.SystemdPath, LinuxDeploymentPlanner.ManifestPath }).ToDictionary(x => x, _ => new RemoteFile(null, null))
};
RemoteSnapshot Apply(RemoteSnapshot snapshot, DeploymentPlan plan)
{
    var files = new Dictionary<string, RemoteFile>(snapshot.Files);
    foreach (var change in plan.Changes) files[change.Path] = new(change.Content, change.Content is null ? null : DataFormat.Hash(change.Content));
    return snapshot with { Files = files };
}
var server = new ServerProfile { Name = "test", Host = "example.test" };
Test("flyout centers on the tray icon using Aulay's 12 DIP anchor", () =>
{
    var monitor = new System.Drawing.Rectangle(0, 0, 1920, 1080);
    var work = new System.Drawing.Rectangle(0, 0, 1920, 1032);
    var icon = new System.Drawing.Rectangle(900, 1040, 24, 24);
    var size = new System.Drawing.Size(380, 400);
    var panel = Portkey.App.FlyoutPlacement.Calculate(icon, monitor, work, size, 1);
    Assert(panel.Left + panel.Width / 2 == icon.Left + icon.Width / 2);
    Assert(panel.Bottom == icon.Top - 12);
});
Test("flyout clamps screen edges and supports top and side taskbars", () =>
{
    var monitor = new System.Drawing.Rectangle(0, 0, 1920, 1080);
    var size = new System.Drawing.Size(380, 600);
    var bottom = Portkey.App.FlyoutPlacement.Calculate(new(1880, 1040, 24, 24), monitor, new(0, 0, 1920, 1032), size, 1);
    Assert(bottom.Right == 1912 && bottom.Bottom <= 1032);
    var top = Portkey.App.FlyoutPlacement.Calculate(new(900, 8, 24, 24), monitor, new(0, 48, 1920, 1032), size, 1);
    Assert(top.Top == 48); // Never overlap a top taskbar when the anchor is inside it.
    var left = Portkey.App.FlyoutPlacement.Calculate(new(8, 500, 24, 24), monitor, new(48, 0, 1872, 1080), size, 1);
    Assert(left.Left == 48 && left.Top == 212);
    var right = Portkey.App.FlyoutPlacement.Calculate(new(1888, 500, 24, 24), monitor, new(0, 0, 1872, 1080), size, 1);
    Assert(right.Right == 1872 && right.Top == 212);
});
Test("flyout scales anchor distance and handles negative monitor coordinates", () =>
{
    var icon = new System.Drawing.Rectangle(-1200, -50, 36, 36);
    var panel = Portkey.App.FlyoutPlacement.Calculate(icon, new(-2560, -1440, 2560, 1440), new(-2560, -1440, 2560, 1380), new(570, 900), 1.5);
    Assert(panel.Left + panel.Width / 2 == icon.Left + icon.Width / 2);
    Assert(panel.Bottom == icon.Top - 18);
});
Test("tray menu shares the panel gap while opening to the right of the icon", () =>
{
    foreach (double scale in new[] { 1.0, 1.5, 2.0 })
    {
        var monitor = new System.Drawing.Rectangle(-2560, 0, 2560, 1440);
        var work = new System.Drawing.Rectangle(-2560, 0, 2560, 1380);
        var icon = new System.Drawing.Rectangle(-1000, 1384, 32, 48);
        var panel = Portkey.App.FlyoutPlacement.Calculate(icon, monitor, work, new(760, 1080), scale);
        var menu = Portkey.App.FlyoutPlacement.Calculate(icon, monitor, work, new(420, 560), scale, centerOnIcon: false);
        Assert(menu.Bottom == panel.Bottom && menu.Left == icon.Left);
        var edge = Portkey.App.FlyoutPlacement.Calculate(new(-32, 1384, 32, 48), monitor, work, new(420, 560), scale, centerOnIcon: false);
        Assert(edge.Right <= work.Right && edge.Bottom == panel.Bottom);
    }
});
Test("default settings have no fake servers", () => Assert(new AppSettings().Servers.Count == 0));
Test("reject option and command injection", () =>
{
    foreach (var host in new[] { "-oProxyCommand=x", "host; touch x", "host\ncommand", "user@host", "$(whoami)", "host space" })
        Throws<ArgumentException>(() => (server with { Host = host }).Validate());
    Throws<ArgumentException>(() => (server with { CheckUrl = "file:///etc/passwd" }).Validate());
    Throws<ArgumentException>(() => (server with { RemotePort = 22 }).Validate());
});
Test("SSH uses strict noninteractive arguments and loopback", () =>
{
    var start = SshCommand.Create(server with { IdentityFile = "C:\\key with space\\id", User = "alice" });
    Assert(!start.UseShellExecute && start.CreateNoWindow);
    Assert(start.ArgumentList.Contains("BatchMode=yes") && start.ArgumentList.Contains("StrictHostKeyChecking=yes"));
    Assert(start.ArgumentList.Contains("C:\\key with space\\id"));
    Assert(!start.ArgumentList.Contains("StrictHostKeyChecking=no"));
});
Test("SSH failure classification", () =>
{
    Assert(SshFailure.Classify("Permission denied (publickey).") == ConnectionState.AuthenticationFailed);
    Assert(SshFailure.Classify("Warning: remote port forwarding failed for listen port 17890") == ConnectionState.PortConflict);
    Assert(SshFailure.Classify("Host key verification failed.") == ConnectionState.HostKeyError);
    Assert(SshFailure.Classify("REMOTE HOST IDENTIFICATION HAS CHANGED") == ConnectionState.HostKeyError);
});
Test("bounded exponential reconnect", () => Assert(SshFailure.RetryDelay(0).TotalSeconds == 3 && SshFailure.RetryDelay(100).TotalSeconds == 120));
Test("fresh HOME installs only owned configuration", () =>
{
    var plan = LinuxDeploymentPlanner.Create(Fresh(), server);
    Assert(plan.Changes.Count == 5);
    Assert(plan.Changes.Any(x => x.Path == ".profile"));
    Assert(plan.Changes.All(x => x.Path.StartsWith(".config/") || x.Path == ".profile" || x.Path == ".bashrc"));
});
Test("repeated deployment is idempotent", () =>
{
    var snapshot = Apply(Fresh(), LinuxDeploymentPlanner.Create(Fresh(), server));
    Assert(LinuxDeploymentPlanner.Create(snapshot, server).Changes.Count == 0);
});
Test("port update changes owned proxy files only", () =>
{
    var snapshot = Apply(Fresh(), LinuxDeploymentPlanner.Create(Fresh(), server));
    var plan = LinuxDeploymentPlanner.Create(snapshot, server with { RemotePort = 20000 });
    Assert(plan.Changes.Count == 3);
    Assert(plan.Changes.All(x => x.Path.StartsWith(".config/")));
});
Test("missing managed file can be repaired", () =>
{
    var snapshot = Apply(Fresh(), LinuxDeploymentPlanner.Create(Fresh(), server));
    snapshot.Files[LinuxDeploymentPlanner.ShellPath] = new(null, null);
    Assert(LinuxDeploymentPlanner.Create(snapshot, server).Changes.Count == 1);
});
Test("Bash existing content and CRLF survive uninstall", () =>
{
    var original = "# user's shell\r\nexport EDITOR=vim\r\n";
    var snapshot = Fresh(); snapshot.Files[".bashrc"] = new(original, DataFormat.Hash(original), 420);
    snapshot = Apply(snapshot, LinuxDeploymentPlanner.Create(snapshot, server));
    Assert(snapshot.Files[".bashrc"].Content!.StartsWith(original));
    snapshot = Apply(snapshot, LinuxDeploymentPlanner.Create(snapshot, server, uninstall: true));
    Assert(snapshot.Files[".bashrc"].Content == original);
});
Test("Bash chooses first active login profile", () =>
{
    var snapshot = Fresh(); snapshot.Files[".bash_profile"] = new("# primary\n", DataFormat.Hash("# primary\n"));
    snapshot.Files[".profile"] = new("# ignored\n", DataFormat.Hash("# ignored\n"));
    var plan = LinuxDeploymentPlanner.Create(snapshot, server);
    Assert(plan.Changes.Any(x => x.Path == ".bash_profile") && plan.Changes.All(x => x.Path != ".profile"));
});
Test("missing trailing newline and initially absent shell files restore exactly", () =>
{
    var snapshot = Fresh();
    snapshot.Files[".bashrc"] = new("# no final newline", DataFormat.Hash("# no final newline"));
    snapshot = Apply(snapshot, LinuxDeploymentPlanner.Create(snapshot, server));
    Assert(LinuxDeploymentPlanner.Create(snapshot, server).Changes.Count == 0);
    snapshot = Apply(snapshot, LinuxDeploymentPlanner.Create(snapshot, server, uninstall: true));
    Assert(snapshot.Files[".bashrc"].Content == "# no final newline");
    Assert(snapshot.Files[".profile"].Content is null);
});
Test("systemd unavailable permits shell-only deployment", () =>
{
    var plan = LinuxDeploymentPlanner.Create(Fresh(false), server);
    Assert(plan.Changes.All(x => x.Path != LinuxDeploymentPlanner.SystemdPath));
    Assert(plan.Warnings.Any(x => x.Contains("不可用")));
});
Test("third party settings are warned and preserved", () =>
{
    var snapshot = Fresh();
    var text = "export HTTPS_PROXY=http://other:8888\n# mihomo data\n";
    snapshot.Files[".bashrc"] = new(text, DataFormat.Hash(text));
    snapshot.Files[".config/environment.d/99-other.conf"] = new("https_proxy=http://other\n", "x");
    var plan = LinuxDeploymentPlanner.Create(snapshot, server);
    Assert(plan.Warnings.Count(x => x.Contains("其他代理")) == 2);
    Assert(plan.Changes.Single(x => x.Path == ".bashrc").Content!.StartsWith(text));
    Assert(plan.Changes.All(x => x.Path != ".config/environment.d/99-other.conf"));
});
Test("unknown file at owned path is never overwritten", () =>
{
    var snapshot = Fresh(); snapshot.Files[LinuxDeploymentPlanner.ShellPath] = new("custom", "x");
    Throws<InvalidOperationException>(() => LinuxDeploymentPlanner.Create(snapshot, server, allowEdited: true));
});
Test("user edits require explicit review and consent", () =>
{
    var snapshot = Apply(Fresh(), LinuxDeploymentPlanner.Create(Fresh(), server));
    snapshot.Files[LinuxDeploymentPlanner.ShellPath] = new("# modified", DataFormat.Hash("# modified"));
    Throws<InvalidOperationException>(() => LinuxDeploymentPlanner.Create(snapshot, server));
    Assert(LinuxDeploymentPlanner.Create(snapshot, server, allowEdited: true).Changes.Count == 1);
});
Test("damaged shell marker refuses ambiguous deletion", () =>
{
    var snapshot = Fresh(); snapshot.Files[".bashrc"] = new(LinuxDeploymentPlanner.Begin + "\ncustom", "x");
    Throws<InvalidOperationException>(() => LinuxDeploymentPlanner.Create(snapshot, server));
});
Test("protocol-specific environment generation", () =>
{
    var socks = LinuxDeploymentPlanner.Variables(server with { Protocol = ProxyProtocol.Socks5 });
    var http = LinuxDeploymentPlanner.Variables(server with { Protocol = ProxyProtocol.Http });
    Assert(socks["https_proxy"].StartsWith("socks5h://") && http["all_proxy"].StartsWith("http://"));
});
Test("unsupported user, shell and version refuse writes", () =>
{
    Throws<InvalidOperationException>(() => LinuxDeploymentPlanner.Create(Fresh() with { Uid = 0 }, server));
    Throws<InvalidOperationException>(() => LinuxDeploymentPlanner.Create(Fresh() with { Shell = "/bin/zsh" }, server));
    var snapshot = Fresh(); snapshot.Files[LinuxDeploymentPlanner.ManifestPath] = new("{\"version\":99}", "x");
    Throws<InvalidOperationException>(() => LinuxDeploymentPlanner.Create(snapshot, server));
});
Test("diagnostic secrets are redacted", () =>
{
    string line = DiagnosticLogger.Redact("https://user:secret@host token=private C:\\Users\\secret\\id_ed25519");
    Assert(!line.Contains("secret") && !line.Contains("private"));
    Assert(line.Contains("https://[credentials]@host"), line);
    Assert(!DiagnosticLogger.Redact("-----BEGIN OPENSSH PRIVATE KEY----- xyz").Contains("xyz"));
    string lower = DiagnosticLogger.Redact("key at c:\\Users\\secret\\id_ed25519 and https://www.gstatic.com/generate_204");
    Assert(!lower.Contains("secret") && lower.Contains("https://www.gstatic.com/generate_204"), lower);
});
Test("editor numbers and duplicate remote ports fail before a tunnel starts", () =>
{
    Throws<ArgumentException>(() => ServerProfile.ParseRequiredInt("abc", "本地代理端口"));
    Throws<ArgumentException>(() => ServerProfile.ParseRequiredInt("1.5", "远程监听端口"));
    Throws<ArgumentException>(() => ServerProfile.ParseOptionalPort("ssh"));
    Assert(ServerProfile.ParseOptionalPort("  ") is null && ServerProfile.ParseRequiredInt(" 17890 ", "远程监听端口") == 17890);
    var first = server with { Host = "Example.TEST", RemotePort = 17890 };
    var second = server with { Id = Guid.NewGuid(), Host = "example.test", RemotePort = 17890 };
    Throws<ArgumentException>(() => ServerProfile.EnsureDistinctRemotePorts([first, second]));
    ServerProfile.EnsureDistinctRemotePorts([first, second with { RemotePort = 17891 }]);
    ServerProfile.EnsureDistinctRemotePorts([first, second with { Host = "other.test" }]);
    Throws<ArgumentException>(() => ServerProfile.EnsureDistinctEndpoints([("work", "192.0.2.10", 17890), ("alias", "192.0.2.10", 17890)]));
    ServerProfile.EnsureDistinctEndpoints([("work", "192.0.2.10", 17890), ("other", "192.0.2.11", 17890)]);
});
Test("saving keeps a live tunnel and still starts an auto-connect profile", () =>
{
    Assert(ConnectionPolicy.ShouldStartAfterSave(true, false));
    Assert(ConnectionPolicy.ShouldStartAfterSave(false, true));
    Assert(!ConnectionPolicy.ShouldStartAfterSave(false, false));
});
Test("only install or repair compares the live profile environment", () =>
{
    Assert(LinuxDeploymentPlanner.VerifiesCurrentProfile("安装 / 修复 Portkey 用户配置"));
    Assert(!LinuxDeploymentPlanner.VerifiesCurrentProfile("卸载 Portkey 用户配置"));
    Assert(!LinuxDeploymentPlanner.VerifiesCurrentProfile("恢复备份 2026"));
});
Test("configured forwards require a dedicated SSH host", () =>
{
    Throws<InvalidOperationException>(() => new ResolvedHost("h", "u", "22", "", true).EnsureDedicatedTunnel());
    new ResolvedHost("h", "u", "22", "", false).EnsureDedicatedTunnel();
});
AsyncTest("settings atomic roundtrip and invalid version preservation", async () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "portkey-settings-" + Guid.NewGuid());
    try
    {
        var store = new SettingsStore(directory);
        Assert((await store.LoadAsync()).Servers.Count == 0);
        await store.SaveAsync(new AppSettings { Servers = [server] });
        Assert((await store.LoadAsync()).Servers.Single().Host == server.Host);
        await store.SaveAsync(new AppSettings());
        Assert(File.Exists(Path.Combine(directory, "settings.backup.json")));
        await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{\"version\":99}");
        try { await store.LoadAsync(); throw new Exception("Expected refusal"); } catch (InvalidDataException) { }
        Assert((await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"))).Contains("99"));
    }
    finally { Directory.Delete(directory, true); }
});
AsyncTest("OpenSSH alias discovery supports Include without evaluating config", async () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "portkey-ssh-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
    try
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "config"), "Host alpha beta\n HostName ignored\nHost * !excluded\nInclude hosts.*\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "hosts.conf"), "Host gamma\nInclude config\n");
        var aliases = new SshConfigReader(new ProcessRunner()).ReadAliases(Path.Combine(directory, "config"));
        Assert(aliases.SequenceEqual(new[] { "alpha", "beta", "gamma" }));
    }
    finally { Directory.Delete(directory, true); }
});
AsyncTest("nested SSH Include is resolved from the including file", async () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "portkey-ssh-" + Guid.NewGuid());
    Directory.CreateDirectory(Path.Combine(directory, "conf.d", "extra"));
    Directory.CreateDirectory(Path.Combine(directory, "extra"));
    try
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "config"), "Include conf.d/work\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "conf.d", "work"), "Host from-nested\nInclude extra/more\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "conf.d", "extra", "more"), "Host from-extra\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "extra", "more"), "Host wrong-root\n");
        var aliases = new SshConfigReader(new ProcessRunner()).ReadAliases(Path.Combine(directory, "config"));
        Assert(aliases.SequenceEqual(new[] { "from-extra", "from-nested" }), string.Join(",", aliases));
    }
    finally { Directory.Delete(directory, true); }
});
AsyncTest("quoted SSH Include can contain a hash", async () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "portkey-ssh-" + Guid.NewGuid());
    Directory.CreateDirectory(directory);
    try
    {
        string included = Path.Combine(directory, "hosts#1.conf");
        await File.WriteAllTextAsync(Path.Combine(directory, "config"), "Include \"hosts#1.conf\"\nHost visible\n");
        await File.WriteAllTextAsync(included, "Host hashed\n");
        var aliases = new SshConfigReader(new ProcessRunner()).ReadAliases(Path.Combine(directory, "config"));
        Assert(aliases.SequenceEqual(new[] { "hashed", "visible" }), string.Join(",", aliases));
    }
    finally { Directory.Delete(directory, true); }
});
AsyncTest("actual OpenSSH resolves host using -G without a network connection", async () =>
{
    var resolved = await new SshConfigReader(new ProcessRunner()).ResolveAsync(server with { Host = "portkey-test.invalid", SshPort = 2222 }, CancellationToken.None);
    Assert(resolved.HostName.Length > 0 && resolved.User.Length > 0 && resolved.Port == "2222");
});
AsyncTest("process timeout terminates only the child created by the runner", async () =>
{
    string marker = Path.Combine(Path.GetTempPath(), "portkey-child-" + Guid.NewGuid());
    try
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("--child-hold"); start.ArgumentList.Add(marker);
        try { await new ProcessRunner().RunAsync(start, null, TimeSpan.FromSeconds(2), CancellationToken.None); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Assert(File.Exists(marker), "Child must start before timeout");
        int pid = int.Parse(await File.ReadAllTextAsync(marker));
        try { using var process = Process.GetProcessById(pid); Assert(process.HasExited, "Owned child survived timeout"); }
        catch (ArgumentException) { }
    }
    finally { File.Delete(marker); }
});
AsyncTest("local proxy waiting is based on an actual loopback socket", async () =>
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    Assert(await ProxyHealthChecker.IsLocalReadyAsync(server with { LocalPort = port }, CancellationToken.None));
    listener.Stop();
    Assert(!await ProxyHealthChecker.IsLocalReadyAsync(server with { LocalPort = port }, CancellationToken.None));
});
AsyncTest("health tests both mixed protocols and rejects wrong HTTP status", async () =>
{
    var remote = new FakeRemote(); var probe = new FakeProbe(); var checker = new ProxyHealthChecker(remote, probe);
    Assert((await checker.CheckAsync(server, CancellationToken.None)).Healthy && remote.Commands.Count == 1);
    Assert(probe.Protocols.SequenceEqual(new[] { "http", "socks5" }));
    probe.Response = 503;
    Assert(!(await checker.CheckAsync(server, CancellationToken.None)).Healthy);
    remote.Listener = "LISTEN 0 128 0.0.0.0:17890 0.0.0.0:*\n";
    Assert((await checker.CheckAsync(server, CancellationToken.None)).Fatal);
    Assert(ProxyHealthChecker.IsLoopbackListener("127.0.0.1%lo:17890", 17890));
    Assert(ProxyHealthChecker.IsLoopbackListener("[::1]%lo:17890", 17890));
    Assert(!ProxyHealthChecker.IsLoopbackListener("0.0.0.0:17890", 17890));
    Assert(!ProxyHealthChecker.IsLoopbackListener("127.0.0.1:17891", 17890));
    probe.Response = 204;
    remote.Listener = "LISTEN 0 128 127.0.0.1%lo:17890 0.0.0.0:*\n";
    var zoned = await checker.CheckAsync(server, CancellationToken.None);
    Assert(!zoned.Fatal && zoned.Healthy, zoned.Detail);
});
AsyncTest("single-protocol health probes use only the configured proxy protocol", async () =>
{
    var probe = new FakeProbe(); var checker = new ProxyHealthChecker(new FakeRemote(), probe);
    Assert((await checker.CheckAsync(server with { Protocol = ProxyProtocol.Http }, CancellationToken.None)).Healthy);
    Assert(probe.Protocols.SequenceEqual(new[] { "http" }));
    probe.Protocols.Clear();
    Assert((await checker.CheckAsync(server with { Protocol = ProxyProtocol.Socks5 }, CancellationToken.None)).Healthy);
    Assert(probe.Protocols.SequenceEqual(new[] { "socks5" }));
});
AsyncTest("blocked SSH probe and timeout stay unhealthy; user cancellation propagates", async () =>
{
    var probe = new FakeProbe { Failure = new HttpRequestException("Proxy handshake failed", new IOException("channel 0: open failed: administratively prohibited")) };
    var checker = new ProxyHealthChecker(new FakeRemote(), probe);
    var result = await checker.CheckAsync(server, CancellationToken.None);
    Assert(!result.Healthy && result.Detail.Contains("direct-tcpip"));
    probe.Failure = new TaskCanceledException();
    Assert(!(await checker.CheckAsync(server, CancellationToken.None)).Healthy);
    using var stop = new CancellationTokenSource(); stop.Cancel();
    try { await checker.CheckAsync(server, stop.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { }
});
async Task Until(Func<bool> condition)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    while (!condition()) await Task.Delay(5, deadline.Token);
}
AsyncTest("state machine waits for proxy then verifies tunnel and pauses", async () =>
{
    var backend = new FakeBackend { Local = false };
    await using var manager = new SshTunnelManager(server, backend, new NullLogger(), TimeSpan.FromMilliseconds(20));
    await manager.StartAsync(); await Until(() => manager.Status.State == ConnectionState.WaitingForProxy);
    Assert(backend.Starts == 0);
    backend.Local = true; manager.Wake();
    await Until(() => manager.Status.State == ConnectionState.Healthy);
    Assert(backend.Starts == 1 && backend.Checks > 0);
    await manager.PauseAsync();
    Assert(manager.Status.State == ConnectionState.Paused && backend.Disposals == 1 && !manager.IsRunning);
    manager.Wake(); await Task.Delay(40); Assert(backend.Starts == 1);
});
AsyncTest("forward established but failed proxy never becomes healthy", async () =>
{
    var backend = new FakeBackend { Health = false };
    await using var manager = new SshTunnelManager(server, backend, new NullLogger(), TimeSpan.FromMilliseconds(20));
    await manager.StartAsync(); await Until(() => manager.Status.State == ConnectionState.ProxyError);
    Assert(backend.Starts == 1);
});
AsyncTest("non-loopback remote listener stops the owned session until manual restart", async () =>
{
    var backend = new FakeBackend { Health = false, Fatal = true };
    await using var manager = new SshTunnelManager(server, backend, new NullLogger(), TimeSpan.FromMilliseconds(20));
    await manager.StartAsync(); await Until(() => manager.Status.State == ConnectionState.Error && !manager.IsRunning);
    Assert(backend.Disposals == 1);
    manager.Wake(); await Task.Delay(40); Assert(backend.Starts == 1);
});
AsyncTest("SSH interruption reconnects when network recovery wakes backoff", async () =>
{
    var backend = new FakeBackend();
    await using var manager = new SshTunnelManager(server, backend, new NullLogger(), TimeSpan.FromMilliseconds(20));
    await manager.StartAsync(); await Until(() => manager.Status.State == ConnectionState.Healthy);
    backend.Last!.End.TrySetResult();
    await Until(() => manager.Status.State == ConnectionState.Reconnecting);
    manager.Wake(); await Until(() => backend.Starts == 2 && manager.Status.State == ConnectionState.Healthy);
    await manager.PauseAsync(); Assert(backend.Disposals == 2);
});
AsyncTest("configuration errors stop retrying and transient failures keep their reason", async () =>
{
    var permanent = new FakeBackend { Permanent = true, Failure = "此 SSH Host 配置中已有端口转发。" };
    await using var stopped = new SshTunnelManager(server, permanent, new NullLogger(), TimeSpan.FromMilliseconds(20));
    await stopped.StartAsync();
    await Until(() => stopped.Status.State == ConnectionState.Error && !stopped.IsRunning);
    await Task.Delay(80);
    Assert(permanent.Starts == 1 && stopped.Status.Detail.Contains("端口转发"), stopped.Status.Detail);
    var transient = new FakeBackend { Failure = "Connection reset by peer" };
    await using var manager = new SshTunnelManager(server, transient, new NullLogger(), TimeSpan.FromMilliseconds(20));
    await manager.StartAsync();
    await Until(() => manager.Status.State == ConnectionState.Reconnecting);
    Assert(manager.Status.Detail.Contains("Connection reset by peer") && manager.Status.Detail.Contains("秒后重试"), manager.Status.Detail);
    await manager.PauseAsync();
});
AsyncTest("local proxy loss waits instead of using the failure backoff", async () =>
{
    var backend = new FakeBackend();
    await using var manager = new SshTunnelManager(server, backend, new NullLogger(), TimeSpan.FromMilliseconds(20));
    await manager.StartAsync();
    await Until(() => manager.Status.State == ConnectionState.Healthy);
    backend.Local = false;
    await Until(() => manager.Status.State is ConnectionState.WaitingForProxy or ConnectionState.Reconnecting);
    Assert(manager.Status.State == ConnectionState.WaitingForProxy, manager.Status.Label + " " + manager.Status.Detail);
    await Task.Delay(60);
    Assert(backend.Starts == 1);
    await manager.PauseAsync();
});
AsyncTest("single instance is per process and a crashed owner does not block the next start", async () =>
{
    async Task<string> WaitFor(string path, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && await File.ReadAllTextAsync(path) == expected) return expected;
            await Task.Delay(20);
        }
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : "missing";
    }
    Process Hold(string mutexName, string path)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--hold-mutex");
        info.ArgumentList.Add(mutexName);
        info.ArgumentList.Add(path);
        return Process.Start(info) ?? throw new Exception("Could not start mutex holder");
    }
    string name = "Portkey.Test." + Guid.NewGuid().ToString("N");
    string marker = Path.Combine(Path.GetTempPath(), "portkey-mutex-" + Guid.NewGuid().ToString("N"));
    using (var owner = new SingleInstance(name))
    {
        Assert(owner.IsFirst, "owner failed " + name);
        using var blocked = Hold(name, marker);
        try { Assert(await WaitFor(marker, "second") == "second", "blocked child was not second"); }
        finally { if (!blocked.HasExited) { blocked.Kill(); blocked.WaitForExit(5000); } }
    }
    string abandonedName = name + ".abandoned";
    string abandonedMarker = marker + ".abandoned";
    using var holder = Hold(abandonedName, abandonedMarker);
    try
    {
        Assert(await WaitFor(abandonedMarker, "first") == "first", "holder was not first");
        holder.Kill();
        await holder.WaitForExitAsync();
        SingleInstance? recovered = null;
        bool acquired = false;
        for (int attempt = 0; attempt < 40 && !acquired; attempt++)
        {
            recovered?.Dispose();
            recovered = new SingleInstance(abandonedName);
            acquired = recovered.IsFirst;
            if (!acquired) await Task.Delay(50);
        }
        using (recovered)
        {
            if (!acquired && !OperatingSystem.IsWindows()) return;
            Assert(acquired, "not recovered");
        }
    }
    finally
    {
        if (!holder.HasExited) { try { holder.Kill(); } catch (InvalidOperationException) { } }
        if (File.Exists(marker)) File.Delete(marker);
        if (File.Exists(abandonedMarker)) File.Delete(abandonedMarker);
    }
});
AsyncTest("authentication and port errors stay visible and cancellable", async () =>
{
    foreach (var text in new[] { "Permission denied (publickey).", "remote port forwarding failed for listen port 17890", "Host key verification failed." })
    {
        var backend = new FakeBackend { Failure = text };
        await using var manager = new SshTunnelManager(server, backend, new NullLogger());
        await manager.StartAsync(); await Until(() => manager.Status.State == SshFailure.Classify(text));
        Assert(backend.Starts == 1); await manager.PauseAsync();
    }
});

if (!OperatingSystem.IsWindows())
{
    AsyncTest("real remote agent: install, repeat, repair, update, restore and uninstall in isolated HOME", async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "portkey-home-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            var transport = new LocalAgentTransport(directory);
            var inspector = new RemoteEnvironmentInspector(transport);
            var executor = new LinuxDeploymentExecutor(inspector, transport);
            var original = "# preserve this\r\nexport EDITOR=vi\r\n";
            await File.WriteAllTextAsync(Path.Combine(directory, ".bashrc"), original);
            var snapshot = await inspector.InspectAsync(server, CancellationToken.None);
            Assert(snapshot.Files[".bashrc"].Content == original);
            await executor.ApplyAsync(server, snapshot, LinuxDeploymentPlanner.Create(snapshot, server), CancellationToken.None);
            async Task<string> Bash(string mode, string file)
            {
                var start = new ProcessStartInfo("/bin/bash") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "--noprofile", "--norc", mode, ". " + DataFormat.ShellQuote(Path.Combine(directory, file)) + "; printf '%s' \"${https_proxy-unset}\"" }) start.ArgumentList.Add(arg);
                start.Environment["HOME"] = directory;
                start.Environment.Remove("BASH_ENV");
                foreach (var key in LinuxDeploymentPlanner.Variables(server).Keys) start.Environment.Remove(key);
                var result = await new ProcessRunner().RunAsync(start, null, TimeSpan.FromSeconds(5), CancellationToken.None);
                Assert(result.ExitCode == 0, result.Error);
                return result.Output;
            }
            Assert(await Bash("-ic", ".bashrc") == "http://127.0.0.1:17890", "Interactive Bash must inherit the generated proxy");
            Assert(await Bash("-lc", ".profile") == "http://127.0.0.1:17890", "Login Bash must inherit the generated proxy");
            Assert(await Bash("-c", ".bashrc") == "unset", "Ordinary noninteractive Bash must be unaffected");
            snapshot = await inspector.InspectAsync(server, CancellationToken.None);
            Assert(LinuxDeploymentPlanner.Create(snapshot, server).Changes.Count == 0);
            Assert(snapshot.Backups.Count == 1);
            var update = LinuxDeploymentPlanner.Create(snapshot, server with { RemotePort = 22000 });
            await executor.ApplyAsync(server, snapshot, update, CancellationToken.None);
            snapshot = await inspector.InspectAsync(server, CancellationToken.None);
            Assert(snapshot.Files[LinuxDeploymentPlanner.ShellPath].Content!.Contains(":22000"));
            var backup = snapshot.Backups[0];
            string restoreReport = await executor.ApplyAsync(server, snapshot, new DeploymentPlan("restore", backup.Restore, [], false), CancellationToken.None);
            Assert(!restoreReport.Contains("未符合预期") && !restoreReport.Contains("systemd 环境与预期不同"), restoreReport);
            snapshot = await inspector.InspectAsync(server, CancellationToken.None);
            Assert(snapshot.Files[LinuxDeploymentPlanner.ShellPath].Content!.Contains(":17890"));
            var stale = LinuxDeploymentPlanner.Create(snapshot, server with { RemotePort = 23000 });
            await File.AppendAllTextAsync(Path.Combine(directory, ".bashrc"), "# user changed after preview\n");
            try { await executor.ApplyAsync(server, snapshot, stale, CancellationToken.None); throw new Exception("Expected concurrency refusal"); } catch (IOException) { }
            snapshot = await inspector.InspectAsync(server, CancellationToken.None);
            await executor.ApplyAsync(server, snapshot, LinuxDeploymentPlanner.Create(snapshot, server, uninstall: true), CancellationToken.None);
            Assert(await File.ReadAllTextAsync(Path.Combine(directory, ".bashrc")) == original + "# user changed after preview\n");
            Assert(!File.Exists(Path.Combine(directory, LinuxDeploymentPlanner.ShellPath)));
        }
        finally { Directory.Delete(directory, true); }
    });
}

int failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + "\n" + error); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;

sealed class NullLogger : IDiagnosticLogger { public void Write(string category, string message) { } }
sealed class FakeRemote : IRemoteTransport
{
    public readonly List<string> Commands = [];
    public string Response = "204";
    public string Listener = "LISTEN 0 128 127.0.0.1:17890 0.0.0.0:*\n";
    public Task<string> RunAsync(ServerProfile server, string command, string? input, TimeSpan timeout, CancellationToken ct)
    { Commands.Add(command); return Task.FromResult(command.StartsWith("ss -H", StringComparison.Ordinal) ? Listener : Response); }
}
sealed class FakeProbe : IProxyProbe
{
    public readonly List<string> Protocols = [];
    public int Response = 204;
    public Exception? Failure;
    public Task<int> GetStatusAsync(ServerProfile server, string protocol, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Protocols.Add(protocol);
        return Failure is null ? Task.FromResult(Response) : Task.FromException<int>(Failure);
    }
}
sealed class FakeBackend : IConnectionBackend
{
    public volatile bool Local = true;
    public bool Health = true;
    public bool Fatal;
    public string? Failure;
    public int Starts, Checks, Disposals;
    public FakeSession? Last;
    public Task<bool> IsLocalReadyAsync(ServerProfile server, CancellationToken ct) => Task.FromResult(Local);
    public Task<HealthResult> CheckAsync(ServerProfile server, CancellationToken ct) { Interlocked.Increment(ref Checks); return Task.FromResult(new HealthResult(Health, "test result", Fatal)); }
    public bool Permanent;
    public Task<ITunnelSession> StartAsync(ServerProfile server, CancellationToken ct)
    {
        Interlocked.Increment(ref Starts);
        if (Permanent) throw new InvalidOperationException(Failure ?? "permanent configuration error");
        if (Failure is not null) throw new IOException(Failure);
        Last = new FakeSession(() => Interlocked.Increment(ref Disposals));
        return Task.FromResult<ITunnelSession>(Last);
    }
}
sealed class FakeSession(Action dispose) : ITunnelSession
{
    public TaskCompletionSource End { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => Task.CompletedTask;
    public Task Completion => End.Task;
    public string Error => "Connection reset by peer";
    public ValueTask DisposeAsync() { dispose(); End.TrySetResult(); return ValueTask.CompletedTask; }
}
sealed class LocalAgentTransport(string home) : IRemoteTransport
{
    public async Task<string> RunAsync(ServerProfile server, string command, string? input, TimeSpan timeout, CancellationToken ct)
    {
        if (!command.StartsWith("python3 - ", StringComparison.Ordinal)) return ""; // Shell verification is not part of this file-transaction test.
        var start = new ProcessStartInfo("/usr/bin/python3") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-"); start.ArgumentList.Add(command[10..].Trim('\''));
        start.Environment["HOME"] = home;
        start.Environment.Remove("XDG_CONFIG_HOME");
        start.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=" + Path.Combine(home, "nonexistent-test-bus");
        start.Environment["XDG_RUNTIME_DIR"] = Path.Combine(home, "runtime");
        var result = await new ProcessRunner().RunAsync(start, input, timeout, ct);
        if (result.ExitCode != 0) throw new IOException(result.Error);
        return result.Output;
    }
}
