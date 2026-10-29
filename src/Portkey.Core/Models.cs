using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Portkey.Core;

public enum ProxyProtocol { Mixed, Http, Socks5 }
public enum ConnectionState { Unconfigured, Paused, WaitingForProxy, Connecting, SshConnected, CheckingProxy, Healthy, ProxyError, Reconnecting, AuthenticationFailed, HostKeyError, PortConflict, Error }

public sealed record ServerProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "新服务器";
    public string Host { get; init; } = "";
    public string User { get; init; } = "";
    public int? SshPort { get; init; }
    public string IdentityFile { get; init; } = "";
    public int LocalPort { get; init; } = 7890;
    public int RemotePort { get; init; } = 17890;
    public ProxyProtocol Protocol { get; init; } = ProxyProtocol.Mixed;
    public bool AutoConnect { get; init; }
    public string CheckUrl { get; init; } = "https://www.gstatic.com/generate_204";
    public int ExpectedStatus { get; init; } = 204;
    public int TimeoutSeconds { get; init; } = 25;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100) throw new ArgumentException("请输入服务器名称（最多 100 字）。");
        // Host is one destination argument, never an option or a remote command.
        if (!Regex.IsMatch(Host, @"\A[A-Za-z0-9][A-Za-z0-9._:\-]*\z") || Host.Length > 253)
            throw new ArgumentException("SSH Host 请使用主机别名、域名或 IP，不要包含用户、空格或命令。");
        if (User.Length > 64 || (User.Length > 0 && !Regex.IsMatch(User, @"\A[a-zA-Z0-9_][a-zA-Z0-9_.\-]*\z")))
            throw new ArgumentException("SSH 用户名格式无效。");
        if (SshPort is < 1 or > 65535 || LocalPort is < 1 or > 65535 || RemotePort is < 1024 or > 65535)
            throw new ArgumentException("端口无效；普通用户远程端口应在 1024–65535 之间。");
        if (IdentityFile.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("密钥路径无效。");
        if (!Enum.IsDefined(Protocol)) throw new ArgumentException("代理协议无效。");
        if (!Uri.TryCreate(CheckUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || CheckUrl.Any(char.IsControl))
            throw new ArgumentException("检测地址必须为不含认证信息的 HTTPS URL。");
        if (TimeoutSeconds is < 3 or > 120 || ExpectedStatus is < 200 or > 399) throw new ArgumentException("检测超时或预期状态码无效。");
    }

    public static int ParseRequiredInt(string text, string label)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            throw new ArgumentException($"{label}请输入整数。");
        return value;
    }

    public static int? ParseOptionalPort(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            throw new ArgumentException("SSH 端口请输入整数，或留空以沿用 SSH 配置。");
        return value;
    }

    public static void EnsureDistinctRemotePorts(IEnumerable<ServerProfile> servers) =>
        EnsureDistinctEndpoints(servers.Select(x => (x.Name, x.Host, x.RemotePort)));

    // Host is the OpenSSH-resolved hostname when available, so two aliases of one machine conflict.
    public static void EnsureDistinctEndpoints(IEnumerable<(string Label, string Host, int RemotePort)> endpoints)
    {
        var duplicate = endpoints.GroupBy(x => (Host: x.Host.Trim().ToLowerInvariant(), x.RemotePort)).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"这些连接会占用同一主机的远程端口 {duplicate.Key.RemotePort}：{string.Join("、", duplicate.Select(x => x.Label))}。请更换远程端口。");
    }
}

public static class ConnectionPolicy
{
    // AutoConnect applies at the next process start. A tunnel already running
    // must be recreated so the saved profile replaces the previous one.
    public static bool ShouldStartAfterSave(bool wasRunning, bool autoConnect) => wasRunning || autoConnect;
}

public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public List<ServerProfile> Servers { get; init; } = [];
}

public sealed record ConnectionStatus(ConnectionState State, string Detail, DateTimeOffset UpdatedAt)
{
    public static ConnectionStatus Create(ConnectionState state, string detail) => new(state, detail, DateTimeOffset.Now);
    public string Label => State switch
    {
        ConnectionState.Paused => "已暂停", ConnectionState.WaitingForProxy => "等待本地代理",
        ConnectionState.Connecting => "正在连接", ConnectionState.SshConnected => "SSH 已连接",
        ConnectionState.CheckingProxy => "正在检测代理", ConnectionState.Healthy => "代理正常",
        ConnectionState.ProxyError => "代理异常", ConnectionState.Reconnecting => "正在重连",
        ConnectionState.AuthenticationFailed => "认证失败", ConnectionState.HostKeyError => "需要验证主机密钥",
        ConnectionState.PortConflict => "远程端口冲突", ConnectionState.Error => "连接异常", _ => "未配置"
    };
}

public static class DataFormat
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string ShellQuote(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";
}

public interface IDiagnosticLogger { void Write(string category, string message); }
public interface IRemoteTransport
{
    Task<string> RunAsync(ServerProfile server, string command, string? input, TimeSpan timeout, CancellationToken cancellationToken);
}

public static class SshFailure
{
    public static ConnectionState Classify(string message)
    {
        if (message.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase) || message.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)) return ConnectionState.HostKeyError;
        if (message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || message.Contains("no supported authentication", StringComparison.OrdinalIgnoreCase)) return ConnectionState.AuthenticationFailed;
        if (message.Contains("remote port forwarding failed", StringComparison.OrdinalIgnoreCase) || message.Contains("cannot listen to port", StringComparison.OrdinalIgnoreCase)) return ConnectionState.PortConflict;
        return ConnectionState.Error;
    }
    public static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(120, 3 * Math.Pow(2, Math.Min(attempt, 6))));
}
