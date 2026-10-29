using System.Text.Json;
using Portkey.Core;
using Portkey.Infrastructure;

// Explicitly destructive opt-in: requires an unused port and a manually cleaned
// legacy configuration. Leaves the verified deployment and an auto-connect profile.
internal static class LiveDeploymentTests
{
    public static async Task<int> RunAsync(string host, int port, string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Use Windows .NET.");
        Directory.CreateDirectory(directory);
        var lines = new List<string>();
        void Record(string value) { lines.Add(DateTimeOffset.Now.ToString("O") + " " + value); Console.WriteLine(lines[^1]); }
        void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = stop.Token;
        var server = new ServerProfile { Name = host, Host = host, RemotePort = port, AutoConnect = true };
        var remote = new SshTransport(new ProcessRunner());
        var inspector = new RemoteEnvironmentInspector(remote);
        var executor = new LinuxDeploymentExecutor(inspector, remote);
        async Task<string> Run(string command) => await remote.RunAsync(server, command, null, TimeSpan.FromSeconds(45), ct);
        var values = LinuxDeploymentPlanner.Variables(server);
        string unset = "env " + string.Join(' ', values.Keys.Select(x => "-u " + x));
        string envScript = "import os,json; print('PORTKEY_ENV='+json.dumps({k:os.environ.get(k) for k in " +
            JsonSerializer.Serialize(values.Keys) + "}))";
        string envCommand = "/usr/bin/python3 -c " + DataFormat.ShellQuote(envScript);
        async Task VerifyShell(string option)
        {
            string result = await Run(unset + " bash " + option + " " + DataFormat.ShellQuote(envCommand));
            string line = result.Split('\n').Single(x => x.StartsWith("PORTKEY_ENV=", StringComparison.Ordinal));
            var actual = JsonSerializer.Deserialize<Dictionary<string, string?>>(line[12..])!;
            Require(values.All(x => actual.GetValueOrDefault(x.Key) == x.Value), "Fresh Bash " + option + " did not load all eight variables.");
            Record("PASS fresh Bash " + option + " loads all eight variables after clearing inherited values");
        }
        try
        {
            var initial = await inspector.InspectAsync(server, ct);
            Require(!initial.Listeners.Split('\n').Any(x => x.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(3)?.EndsWith(":" + port) == true), "Test port is occupied; stop its identified legacy owner first.");
            Require(initial.Files[LinuxDeploymentPlanner.ManifestPath].Content is null, "This test requires a first deployment.");
            Record("PASS remote port has no listener and no existing Portkey manifest");
            Record("LOCAL " + await ProxyHealthChecker.CheckLocalAsync(server, ct));
            foreach (string protocol in new[] { "http", "socks5" })
            {
                bool failed = false;
                try { await new SshProxyProbe().GetStatusAsync(server, protocol, ct); }
                catch (Exception error) when (error is HttpRequestException or IOException) { failed = true; }
                Require(failed, "Absent remote tunnel unexpectedly passed " + protocol);
                Record("PASS absent remote tunnel fails real " + protocol + " request while local proxy remains available");
            }

            await using var logger = new DiagnosticLogger(Path.Combine(directory, "connection"));
            await using var manager = new SshTunnelManager(server, new OpenSshBackend(remote, logger), logger);
            async Task Healthy()
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(65));
                while (manager.Status.State != ConnectionState.Healthy) await Task.Delay(150, timeout.Token);
                Record("PASS production tunnel manager healthy: " + manager.Status.Detail);
            }
            await manager.StartAsync();
            await Healthy();
            var snapshot = await inspector.InspectAsync(server, ct);
            var plan = LinuxDeploymentPlanner.Create(snapshot, server);
            Require(plan.Changes.Count == 5, "Unexpected first-deployment file count: " + plan.Changes.Count);
            // Keep the report free of unrelated shell file contents and secrets.
            Record("PLAN " + string.Join(", ", plan.Changes.Select(x => x.Path)));
            Record("APPLY " + await executor.ApplyAsync(server, snapshot, plan, ct));
            var deployed = await inspector.InspectAsync(server, ct);
            Require(LinuxDeploymentPlanner.Create(deployed, server).Changes.Count == 0, "Repeat deployment is not idempotent.");
            Require(deployed.Files[".profile"].Content == initial.Files[".profile"].Content, "Inactive .profile was unexpectedly changed.");
            Record("PASS committed files verified; repeat deployment changes zero files; inactive .profile preserved");
            await VerifyShell("-lc");
            await VerifyShell("-ic");
            var systemd = (await Run("systemctl --user show-environment")).Split('\n').Select(x => x.Split('=', 2))
                .Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
            Require(values.All(x => systemd.GetValueOrDefault(x.Key) == x.Value), "systemd environment mismatch.");
            Record("PASS systemd user manager contains all eight proxy variables");
            string unit = "portkey-persistence-test-" + Guid.NewGuid().ToString("N");
            string service = await Run("systemd-run --user --quiet --wait --pipe --collect --unit=" + unit + " " + envCommand);
            var serviceEnv = JsonSerializer.Deserialize<Dictionary<string, string?>>(service.Split('\n').Single(x => x.StartsWith("PORTKEY_ENV=", StringComparison.Ordinal))[12..])!;
            Require(values.All(x => serviceEnv.GetValueOrDefault(x.Key) == x.Value), "New systemd user service did not inherit proxy variables.");
            Record("PASS newly started transient user service inherits persisted proxy environment");
            string request = "/usr/bin/python3 -c " + DataFormat.ShellQuote("import urllib.request; r=urllib.request.urlopen('https://www.gstatic.com/generate_204',timeout=25); assert r.status==204; print('PORTKEY_HTTPS='+str(r.status))");
            Require((await Run(unset + " bash -lc " + DataFormat.ShellQuote(request))).Contains("PORTKEY_HTTPS=204", StringComparison.Ordinal), "Fresh Linux application HTTPS request failed.");
            Record("PASS fresh Linux login uses persisted environment for real HTTPS 204 with Python default certificate verification");
            await manager.PauseAsync();
            Require((await Run("ss -H -lnt 'sport = :" + port + "'")).Trim().Length == 0, "Paused tunnel still listening.");
            var paused = await inspector.InspectAsync(server, ct);
            Require(deployed.Files.All(x => paused.Files[x.Key].Hash == x.Value.Hash), "Persistent files changed after tunnel stop.");
            await VerifyShell("-lc");
            Record("PASS persistent files and fresh login environment survive tunnel stop");
            await manager.StartAsync();
            await Healthy();
            await manager.PauseAsync();
            var store = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Portkey"));
            var settings = await store.LoadAsync(ct);
            Require(!settings.Servers.Any(x => x.Host == host && x.RemotePort == port), "A profile for this endpoint already exists; refusing duplicate.");
            await store.SaveAsync(settings with { Servers = [.. settings.Servers, server] }, ct);
            Record("PASS production SettingsStore saved auto-connect profile for desktop application handoff");
            Record("RESULT PASS; deployment remains installed; test tunnel stopped for desktop application handoff");
            return 0;
        }
        catch (Exception error) { Record("RESULT FAIL " + DiagnosticLogger.Redact(error.ToString())); return 1; }
        finally { await File.WriteAllLinesAsync(Path.Combine(directory, "deployment-test.log"), lines, CancellationToken.None); }
    }
}
