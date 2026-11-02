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
        // A windowed process has no console, so a null pipe encoding is code page 0
        // (the ANSI code page). GBK makes Python reject the first non-ASCII line of agent.py.
        UseUtf8Text(start);
        return start;
    }

    public static readonly Encoding Utf8Text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    public static void UseUtf8Text(ProcessStartInfo start)
    {
        if (start.RedirectStandardInput) start.StandardInputEncoding ??= Utf8Text;
        if (start.RedirectStandardOutput) start.StandardOutputEncoding ??= Utf8Text;
        if (start.RedirectStandardError) start.StandardErrorEncoding ??= Utf8Text;
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
        SshCommand.UseUtf8Text(start);
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
public sealed record SpecifiedSshSettings(string? User, int? Port, string? IdentityFile);

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
        Visit(path, line =>
        {
            var tokens = Tokens(line);
            if (tokens.Length < 2 || !tokens[0].Equals("Host", StringComparison.OrdinalIgnoreCase)) return;
            foreach (var host in tokens.Skip(1))
                if (Regex.IsMatch(host, @"\A[A-Za-z0-9][A-Za-z0-9._:\-]*\z")) result.Add(host);
        });
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Returns User, Port, and IdentityFile only when the SSH configuration sets them for the alias. Built-in defaults are omitted.
    public SpecifiedSshSettings ReadSpecified(string path, string alias)
    {
        string? exactUser = null, broadUser = null, exactIdentity = null, broadIdentity = null;
        int? exactPort = null, broadPort = null;
        bool applies = true, exact = false;
        Visit(path, line =>
        {
            var tokens = Tokens(line);
            if (tokens.Length == 0) return;
            if (tokens[0].Equals("Host", StringComparison.OrdinalIgnoreCase) || tokens[0].Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                var patterns = tokens[0].Equals("Host", StringComparison.OrdinalIgnoreCase)
                    ? tokens.Skip(1).SelectMany(SplitPatterns).ToArray()
                    : null;
                if (patterns is not null)
                {
                    applies = HostMatches(alias, patterns);
                    exact = applies && patterns.Any(x => IsExact(x, alias));
                }
                else
                {
                    applies = MatchApplies(tokens.Skip(1).ToArray(), alias, out exact);
                }
                return;
            }
            if (!applies || tokens.Length < 2) return;
            if (tokens[0].Equals("User", StringComparison.OrdinalIgnoreCase))
                Assign(ref exactUser, ref broadUser, tokens[1].Trim(), exact);
            else if (tokens[0].Equals("Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(tokens[1], out int port) && port is >= 1 and <= 65535)
            {
                if (exact) exactPort ??= port; else broadPort ??= port;
            }
            else if (tokens[0].Equals("IdentityFile", StringComparison.OrdinalIgnoreCase))
                Assign(ref exactIdentity, ref broadIdentity, tokens[1], exact);
        });
        string? user = exactUser ?? broadUser;
        int? port = exactPort ?? broadPort;
        string? identity = ExpandIdentity(exactIdentity ?? broadIdentity, alias, user, port);
        if (string.IsNullOrWhiteSpace(user)) user = null;
        if (string.IsNullOrWhiteSpace(identity)) identity = null;
        return new(user, port, identity);
    }

    private static void Assign(ref string? exactValue, ref string? broadValue, string value, bool exact)
    {
        if (value.Length == 0) return;
        if (exact) exactValue ??= value; else broadValue ??= value;
    }

    private void Visit(string path, Action<string> consume)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Read(string current, int depth)
        {
            if (depth > 8 || !File.Exists(current) || !visited.Add(Path.GetFullPath(current)) || visited.Count > 128) return;
            if (new FileInfo(current).Length > 1024 * 1024) return;
            foreach (var line in File.ReadLines(current))
            {
                consume(line);
                var tokens = Tokens(line);
                if (tokens.Length < 2 || !tokens[0].Equals("Include", StringComparison.OrdinalIgnoreCase)) continue;
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
    }

    private static string[] Tokens(string line) =>
        Regex.Matches(WithoutComment(line), "\"[^\"]*\"|'[^']*'|[^\\s=]+").Select(x => x.Value.Trim('"', '\'')).ToArray();

    private static IEnumerable<string> SplitPatterns(string token) =>
        token.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsExact(string pattern, string alias) =>
        pattern.Length > 0 && pattern[0] != '!' && pattern.IndexOfAny(['*', '?', '[']) < 0 && pattern.Equals(alias, StringComparison.OrdinalIgnoreCase);

    private static bool HostMatches(string alias, IReadOnlyList<string> patterns)
    {
        bool positive = false;
        foreach (var pattern in patterns)
        {
            if (pattern.Length == 0) continue;
            if (pattern[0] == '!')
            {
                if (pattern.Length > 1 && Glob(alias, pattern[1..])) return false;
            }
            else if (Glob(alias, pattern)) positive = true;
        }
        return positive;
    }

    private static bool MatchApplies(string[] tokens, string alias, out bool exact)
    {
        exact = false;
        if (tokens.Length == 1 && tokens[0].Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
        if (tokens.Length == 0) return false;
        bool matched = true, saw = false, allExact = true;
        for (int i = 0; i < tokens.Length;)
        {
            if (!tokens[i].Equals("host", StringComparison.OrdinalIgnoreCase) && !tokens[i].Equals("originalhost", StringComparison.OrdinalIgnoreCase))
                return false;
            i++;
            var patterns = new List<string>();
            while (i < tokens.Length && !tokens[i].Equals("host", StringComparison.OrdinalIgnoreCase) && !tokens[i].Equals("originalhost", StringComparison.OrdinalIgnoreCase))
                patterns.AddRange(SplitPatterns(tokens[i++]));
            if (patterns.Count == 0) return false;
            saw = true;
            if (!HostMatches(alias, patterns)) matched = false;
            if (!patterns.Any(x => IsExact(x, alias))) allExact = false;
        }
        exact = saw && matched && allExact;
        return saw && matched;
    }

    private static bool Glob(string text, string pattern) => Glob(text.AsSpan(), pattern.AsSpan());

    private static bool Glob(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        if (pattern.IsEmpty) return text.IsEmpty;
        if (pattern[0] == '*')
        {
            for (int i = 0; i <= text.Length; i++)
                if (Glob(text[i..], pattern[1..])) return true;
            return false;
        }
        if (text.IsEmpty) return false;
        if (pattern[0] == '?') return Glob(text[1..], pattern[1..]);
        if (pattern[0] == '[')
        {
            int end = pattern.IndexOf(']');
            if (end > 1 && ClassContains(pattern[1..end], text[0])) return Glob(text[1..], pattern[(end + 1)..]);
            if (end > 1) return false;
        }
        return CharsEqual(pattern[0], text[0]) && Glob(text[1..], pattern[1..]);
    }

    private static bool ClassContains(ReadOnlySpan<char> body, char value)
    {
        bool negate = body.Length > 0 && body[0] is '!' or '^';
        if (negate) body = body[1..];
        bool found = false;
        for (int i = 0; i < body.Length; i++)
        {
            if (i + 2 < body.Length && body[i + 1] == '-')
            {
                char c = char.ToLowerInvariant(value);
                if (c >= char.ToLowerInvariant(body[i]) && c <= char.ToLowerInvariant(body[i + 2])) found = true;
                i += 2;
            }
            else if (CharsEqual(body[i], value)) found = true;
        }
        return negate ? !found : found;
    }

    private static bool CharsEqual(char left, char right) => char.ToLowerInvariant(left) == char.ToLowerInvariant(right);

    private static string? ExpandIdentity(string? value, string alias, string? user, int? port)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().Trim('"');
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (value.StartsWith("~/") || value.StartsWith("~\\"))
            value = Path.Combine(home, value[2..].Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        else if (value == "~") value = home;
        var builder = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '%' || i + 1 >= value.Length) { builder.Append(value[i]); continue; }
            char token = value[++i];
            string? replacement = token switch
            {
                '%' => "%",
                'd' => home,
                'u' => Environment.UserName,
                'h' or 'n' => alias,
                'r' => user,
                'p' => port?.ToString(),
                _ => null
            };
            if (replacement is null) builder.Append('%').Append(token);
            else builder.Append(replacement);
        }
        string expanded = builder.ToString();
        if (Path.DirectorySeparatorChar == '\\' && (expanded.Contains(':') || expanded.StartsWith(home, StringComparison.OrdinalIgnoreCase)))
            expanded = expanded.Replace('/', '\\');
        return expanded;
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
