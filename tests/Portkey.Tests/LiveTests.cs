using System.Text.Json;
using Portkey.Core;
using Portkey.Infrastructure;

// Explicit opt-in only. Never applies a deployment, reloads systemd, or stops
// existing tunnels. All fault injection disposes sessions created by this run.
internal static class LiveTests
{
    public static async Task<int> RunAsync(string host, int port, string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run live tests with Windows .NET and Windows OpenSSH.");
        var log = new LiveLogger();
        Directory.CreateDirectory(directory);
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = stop.Token;
        var server = new ServerProfile { Name = "Portkey live test", Host = host, RemotePort = port };
        var remote = new SshTransport(new ProcessRunner());
        int failures = 0;
        async Task Check(string name, Func<Task> run)
        {
            try { await run(); log.Write("PASS", name); }
            catch (Exception error) { failures++; log.Write("FAIL", name + ": " + error.Message); }
        }
        void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
        async Task<string> Listeners() => await remote.RunAsync(server, "ss -H -lnt", null, TimeSpan.FromSeconds(15), ct);
        string before = await Listeners();
        Require(!before.Split('\n').Any(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(3)?.EndsWith(":" + port) == true), "Selected test port is already occupied; no tunnel created.");
        string? inspectorBefore = null;
        await Check("key authentication and effective SSH configuration", async () =>
        {
            var config = await new SshConfigReader(new ProcessRunner()).ResolveAsync(server, ct);
            log.Write("SSH", $"{config.User}@{config.HostName}:{config.Port}");
            var user = await remote.RunAsync(server, "id -un", null, TimeSpan.FromSeconds(15), ct);
            Require(user.Trim() == config.User, "Unexpected remote user.");
        });
        var inspector = new RemoteEnvironmentInspector(remote);
        await Check("read-only environment inspection and deployment preview", async () =>
        {
            var snapshot = await inspector.InspectAsync(server, ct);
            inspectorBefore = JsonSerializer.Serialize(snapshot.Files.ToDictionary(x => x.Key, x => x.Value.Hash));
            log.Write("ENV", $"user={snapshot.User}; shell={snapshot.Shell}; systemd={snapshot.Systemd}; Portkey manifest present={snapshot.Files[LinuxDeploymentPlanner.ManifestPath].Content is not null}");
            var plan = LinuxDeploymentPlanner.Create(snapshot, server);
            await File.WriteAllTextAsync(Path.Combine(directory, "deployment-preview.txt"), plan.Preview(snapshot), ct);
            log.Write("PLAN", $"{plan.Changes.Count} proposed file changes; not applied. Preview saved locally.");
        });
        await Check("Windows local proxy HTTP and SOCKS5 HTTPS requests", async () =>
        {
            var result = await ProxyHealthChecker.CheckLocalAsync(server, ct);
            log.Write("LOCAL", result);
            Require(result.Split('\n').Count(x => x.Contains("HTTP 204", StringComparison.Ordinal)) == 2, "Both local protocols must return 204.");
        });

        var backend = new CapturingBackend(new OpenSshBackend(remote, log));
        await using (var manager = new SshTunnelManager(server, backend, log, TimeSpan.FromSeconds(5)))
        {
            async Task Until(Func<bool> condition, int seconds = 60)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
                while (!condition()) await Task.Delay(100, timeout.Token);
            }
            try
            {
                await Check("real reverse forwarding established", async () =>
                {
                    await manager.StartAsync();
                    await Until(() => backend.Last?.Ready.IsCompletedSuccessfully == true);
                    var listeners = await Listeners();
                    Require(listeners.Contains("127.0.0.1:" + port, StringComparison.Ordinal), "Remote loopback listener missing.");
                });
                await Check("remote HTTP and SOCKS5 end-to-end health", async () =>
                {
                    var result = await new ProxyHealthChecker(remote).CheckAsync(server, ct);
                    log.Write("HEALTH", result.Detail);
                    Require(result.Healthy, "Remote proxy health did not pass.");
                    await Until(() => manager.Status.State == ConnectionState.Healthy);
                });
                await Check("real occupied-port failure preserves the existing tunnel", async () =>
                {
                    await using var duplicate = await backend.Inner.StartAsync(server, ct);
                    await duplicate.Completion.WaitAsync(TimeSpan.FromSeconds(30), ct);
                    Require(!duplicate.Ready.IsCompletedSuccessfully, "Duplicate tunnel unexpectedly succeeded.");
                    Require(SshFailure.Classify(duplicate.Error) == ConnectionState.PortConflict, duplicate.Error);
                    Require((await Listeners()).Contains("127.0.0.1:" + port, StringComparison.Ordinal), "Original listener disappeared.");
                });
                await Check("owned SSH process termination triggers real reconnect", async () =>
                {
                    var session = backend.Last ?? throw new InvalidOperationException("No owned session.");
                    int starts = backend.Starts;
                    await session.DisposeAsync();
                    await Until(() => backend.Starts > starts && backend.Last?.Ready.IsCompletedSuccessfully == true);
                    await Until(() => manager.Status.State == ConnectionState.Healthy);
                    Require((await Listeners()).Contains("127.0.0.1:" + port, StringComparison.Ordinal), "Reconnected listener missing.");
                });
                await Check("manual pause stops retry and releases only the test port", async () =>
                {
                    await manager.PauseAsync();
                    int starts = backend.Starts;
                    manager.Wake();
                    await Task.Delay(TimeSpan.FromSeconds(4), ct);
                    Require(backend.Starts == starts && !manager.IsRunning, "Paused connection retried.");
                    Require(!(await Listeners()).Contains("127.0.0.1:" + port, StringComparison.Ordinal), "Test listener survived pause.");
                });
            }
            finally { await manager.PauseAsync(); }
        }
        await Check("missing remote listener cannot pass using the healthy Windows proxy", async () =>
        {
            Require(await ProxyHealthChecker.IsLocalReadyAsync(server, ct), "Local proxy should still be reachable.");
            try { await new SshProxyProbe().GetStatusAsync(server, "http", ct); }
            catch (Exception error) when (error is HttpRequestException or IOException) { return; }
            throw new InvalidOperationException("Probe unexpectedly bypassed the absent remote listener.");
        });
        await Check("wrong target HTTP status stays unhealthy while real SSH remains connected", async () =>
        {
            var wrongStatus = server with { ExpectedStatus = 200 };
            var unhealthyBackend = new CapturingBackend(new OpenSshBackend(remote, log));
            await using var unhealthy = new SshTunnelManager(wrongStatus, unhealthyBackend, log);
            await unhealthy.StartAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            while (unhealthy.Status.State != ConnectionState.ProxyError) await Task.Delay(100, timeout.Token);
            Require(unhealthyBackend.Last?.Completion.IsCompleted == false, "SSH should still be connected.");
            Require(unhealthy.Status.Detail.Contains("204", StringComparison.Ordinal), unhealthy.Status.Detail);
        });
        await Check("pre-existing remote listeners and user configuration preserved", async () =>
        {
            string after = await Listeners();
            foreach (var line in before.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var address = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(3);
                if (address is not null) Require(after.Contains(address, StringComparison.Ordinal), "Pre-existing listener missing: " + address);
            }
            if (inspectorBefore is not null)
            {
                var snapshot = await inspector.InspectAsync(server, ct);
                Require(JsonSerializer.Serialize(snapshot.Files.ToDictionary(x => x.Key, x => x.Value.Hash)) == inspectorBefore, "Remote configuration digests changed.");
            }
        });
        log.Write("RESULT", $"failures={failures}; host={host}; test remote port={port}; no deployment applied");
        await File.WriteAllTextAsync(Path.Combine(directory, "live-test.log"), log.Text, CancellationToken.None);
        return failures == 0 ? 0 : 1;
    }

    private sealed class LiveLogger : IDiagnosticLogger
    {
        private readonly object sync = new();
        private readonly List<string> lines = [];
        public string Text { get { lock (sync) return string.Join(Environment.NewLine, lines); } }
        public void Write(string category, string message)
        {
            string line = $"{DateTimeOffset.Now:O} [{category}] {DiagnosticLogger.Redact(message)}";
            lock (sync) { lines.Add(line); Console.WriteLine(line); }
        }
    }
    private sealed class CapturingBackend(IConnectionBackend inner) : IConnectionBackend
    {
        public IConnectionBackend Inner => inner;
        public int Starts;
        public OwnedSession? Last;
        public Task<bool> IsLocalReadyAsync(ServerProfile s, CancellationToken ct) => inner.IsLocalReadyAsync(s, ct);
        public Task<HealthResult> CheckAsync(ServerProfile s, CancellationToken ct) => inner.CheckAsync(s, ct);
        public async Task<ITunnelSession> StartAsync(ServerProfile s, CancellationToken ct)
        {
            var session = new OwnedSession(await inner.StartAsync(s, ct));
            Last = session; Interlocked.Increment(ref Starts); return session;
        }
    }
    private sealed class OwnedSession(ITunnelSession inner) : ITunnelSession
    {
        private readonly object sync = new();
        private Task? disposal;
        public Task Ready => inner.Ready;
        public Task Completion => inner.Completion;
        public string Error => inner.Error;
        public ValueTask DisposeAsync() { lock (sync) return new(disposal ??= inner.DisposeAsync().AsTask()); }
    }
}
