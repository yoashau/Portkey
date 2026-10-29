using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Portkey.Core;

namespace Portkey.Infrastructure;

public static class SshCommand
{
    public static string Executable => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe") : "/usr/bin/ssh";

    public static ProcessStartInfo Create(ServerProfile server, bool interactive = false)
    {
        server.Validate();
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = !interactive,
            RedirectStandardInput = !interactive, RedirectStandardOutput = !interactive, RedirectStandardError = !interactive };
        foreach (var arg in new[] { "-o", "ConnectTimeout=10", "-o", "ConnectionAttempts=1", "-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=3", "-o", "ControlMaster=no", "-o", "ControlPath=none", "-o", "RemoteCommand=none", "-o", "ForwardAgent=no", "-o", "ForwardX11=no", "-o", "PermitLocalCommand=no" }) start.ArgumentList.Add(arg);
        if (!interactive)
            foreach (var arg in new[] { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes" }) start.ArgumentList.Add(arg);
        else
            foreach (var arg in new[] { "-o", "BatchMode=no", "-o", "StrictHostKeyChecking=ask" }) start.ArgumentList.Add(arg);
        if (server.SshPort.HasValue) { start.ArgumentList.Add("-p"); start.ArgumentList.Add(server.SshPort.Value.ToString()); }
        if (server.User.Length > 0) { start.ArgumentList.Add("-l"); start.ArgumentList.Add(server.User); }
        if (server.IdentityFile.Length > 0) { start.ArgumentList.Add("-i"); start.ArgumentList.Add(server.IdentityFile); start.ArgumentList.Add("-o"); start.ArgumentList.Add("IdentitiesOnly=yes"); }
        start.Environment["LC_ALL"] = "C";
        return start;
    }

    public static void AddDestination(ProcessStartInfo start, string host) { start.ArgumentList.Add("--"); start.ArgumentList.Add(host); }
    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}

public sealed class ProcessRunner
{
    public async Task<(int ExitCode, string Output, string Error)> RunAsync(ProcessStartInfo start, string? input, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var job = ProcessJob.Attach(process);
        using var cancellation = deadline.Token.Register(() => SshCommand.Kill(process));
        var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var error = ReadBoundedAsync(process.StandardError, deadline.Token);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        catch
        {
            SshCommand.Kill(process);
            // Kill requests termination asynchronously. Reap the owned child
            // before returning cancellation to the caller.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            try { await Task.WhenAll(output, error); } catch (Exception) { }
            throw;
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, ct)) > 0)
        {
            if (builder.Length + count > 4 * 1024 * 1024) throw new IOException("子进程输出超过限制。");
            builder.Append(buffer, 0, count);
        }
        return builder.ToString();
    }
}

public sealed class SshTransport(ProcessRunner runner) : IRemoteTransport
{
    public async Task<string> RunAsync(ServerProfile server, string command, string? input, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = SshCommand.Create(server);
        // Ignore all configured forwards for short-lived inspection/health sessions.
        start.ArgumentList.Add("-o"); start.ArgumentList.Add("ClearAllForwardings=yes");
        start.ArgumentList.Add("-T");
        SshCommand.AddDestination(start, server.Host);
        start.ArgumentList.Add(command);
        var result = await runner.RunAsync(start, input, timeout, cancellationToken);
        if (result.ExitCode != 0) throw new IOException(DiagnosticLogger.Redact(result.Error));
        return result.Output;
    }
}

public sealed record ResolvedHost(string HostName, string User, string Port, string IdentityFile, bool HasConfiguredForwards)
{
    public const string ConfiguredForwardsMessage = "此 SSH Host 配置中已有端口转发。请建立不含 LocalForward / RemoteForward / DynamicForward 的专用 Host 别名，以免重复建立其他转发。";
    public void EnsureDedicatedTunnel()
    {
        if (HasConfiguredForwards) throw new InvalidOperationException(ConfiguredForwardsMessage);
    }
}
public sealed class SshConfigReader(ProcessRunner runner)
{
    public async Task<ResolvedHost> ResolveAsync(ServerProfile server, CancellationToken ct)
    {
        var start = SshCommand.Create(server);
        start.ArgumentList.Add("-G");
        SshCommand.AddDestination(start, server.Host);
        var result = await runner.RunAsync(start, null, TimeSpan.FromSeconds(15), ct);
        if (result.ExitCode != 0) throw new IOException(result.Error);
        var entries = result.Output.Split('\n').Select(x => x.Trim().Split(' ', 2))
            .Where(x => x.Length == 2).GroupBy(x => x[0], StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First()[1], StringComparer.OrdinalIgnoreCase);
        return new(entries.GetValueOrDefault("hostname", server.Host), entries.GetValueOrDefault("user", ""), entries.GetValueOrDefault("port", "22"), entries.GetValueOrDefault("identityfile", ""),
            entries.Keys.Any(x => x is "localforward" or "remoteforward" or "dynamicforward"));
    }
    public IReadOnlyList<string> ReadAliases(string path)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Read(string current, int depth)
        {
            if (depth > 8 || !File.Exists(current) || !visited.Add(Path.GetFullPath(current)) || visited.Count > 128) return;
            if (new FileInfo(current).Length > 1024 * 1024) return;
            foreach (var line in File.ReadLines(current))
            {
                var tokens = Regex.Matches(WithoutComment(line), "\"[^\"]*\"|'[^']*'|[^\\s=]+").Select(x => x.Value.Trim('"', '\'')).ToArray();
                if (tokens.Length < 2) continue;
                if (tokens[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
                    foreach (var host in tokens.Skip(1))
                        if (Regex.IsMatch(host, @"\A[A-Za-z0-9][A-Za-z0-9._:\-]*\z")) result.Add(host);
                if (tokens[0].Equals("Include", StringComparison.OrdinalIgnoreCase))
                    foreach (var pattern in tokens.Skip(1))
                    {
                        string expanded = pattern.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), pattern[2..]) : pattern;
                        // OpenSSH resolves a relative Include against the file that contains it, not the top-level config.
                        if (!Path.IsPathRooted(expanded)) expanded = Path.Combine(Path.GetDirectoryName(current)!, expanded);
                        var directory = Path.GetDirectoryName(expanded);
                        if (directory is not null && Directory.Exists(directory))
                            foreach (var file in Directory.EnumerateFiles(directory, Path.GetFileName(expanded)).Take(128)) Read(file, depth + 1);
                    }
            }
        }
        Read(path, 0);
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string WithoutComment(string line)
    {
        var builder = new StringBuilder();
        char quote = '\0';
        foreach (char c in line)
        {
            if (quote == '\0' && c == '#') break;
            if ((c == '"' || c == '\'') && quote == '\0') quote = c;
            else if (c == quote) quote = '\0';
            builder.Append(c);
        }
        return builder.ToString();
    }
}
