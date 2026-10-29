using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Portkey.Core;

namespace Portkey.Infrastructure;

public sealed record HealthResult(bool Healthy, string Detail, bool Fatal = false);
public interface ITunnelSession : IAsyncDisposable
{
    Task Ready { get; }
    Task Completion { get; }
    string Error { get; }
}
public interface IConnectionBackend
{
    Task<bool> IsLocalReadyAsync(ServerProfile server, CancellationToken ct);
    Task<ITunnelSession> StartAsync(ServerProfile server, CancellationToken ct);
    Task<HealthResult> CheckAsync(ServerProfile server, CancellationToken ct);
}

public sealed class ProxyHealthChecker(IRemoteTransport remote, IProxyProbe? probe = null)
{
    private readonly IProxyProbe proxyProbe = probe ?? new SshProxyProbe();
    public static async Task<string> CheckLocalAsync(ServerProfile server, CancellationToken ct)
    {
        server.Validate();
        var reports = new List<string>();
        foreach (var protocol in new[] { "http", "socks5" })
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(server.TimeoutSeconds));
            using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"{protocol}://127.0.0.1:{server.LocalPort}"), UseProxy = true, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(Math.Min(10, server.TimeoutSeconds)) };
            using var client = new HttpClient(handler);
            try
            {
                using var response = await client.GetAsync(server.CheckUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                reports.Add($"{protocol}: HTTP {(int)response.StatusCode}" + ((int)response.StatusCode == server.ExpectedStatus ? "（本地代理实际访问通过）" : "（与预期不同，请检查目标或出口）"));
            }
            catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !ct.IsCancellationRequested)
            { reports.Add($"{protocol}: 检测失败，{DiagnosticLogger.Redact(error.Message)}"); }
        }
        return string.Join('\n', reports);
    }
    public static bool IsLoopbackListener(string address, int port)
    {
        string suffix = ":" + port;
        if (!address.EndsWith(suffix, StringComparison.Ordinal)) return false;
        string host = address[..^suffix.Length];
        int zone = host.IndexOf('%');
        if (zone >= 0) host = host[..zone];
        return host is "127.0.0.1" or "[::1]";
    }

    public static async Task<bool> IsLocalReadyAsync(ServerProfile server, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var socket = new TcpClient();
        try { await socket.ConnectAsync(IPAddress.Loopback, server.LocalPort, timeout.Token); return true; }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested) { return false; }
    }

    public async Task<HealthResult> CheckAsync(ServerProfile server, CancellationToken ct)
    {
        try
        {
            var command = "ss -H -lnt " + DataFormat.ShellQuote("sport = :" + server.RemotePort);
            var listeners = await remote.RunAsync(server, command, null, TimeSpan.FromSeconds(15), ct);
            var addresses = listeners.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 4).Select(parts => parts[3]).ToArray();
            if (addresses.Length == 0) return new(false, "远程未发现代理监听端口；无法确认转发可用。");
            if (addresses.Any(address => !IsLoopbackListener(address, server.RemotePort)))
                return new(false, "远程端口存在非回环监听。为避免向外网开放，已停止本连接；请管理员检查 GatewayPorts，再手动连接。", true);
        }
        catch (IOException error) { return new(false, "无法检查远程监听边界：" + error.Message); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "检查远程监听超时。"); }
        // For mixed ports both protocols must work. A healthy SSH process alone is
        // never sufficient to mark the connection healthy.
        var protocols = server.Protocol == ProxyProtocol.Mixed ? new[] { "http", "socks5" }
            : new[] { server.Protocol == ProxyProtocol.Http ? "http" : "socks5" };
        foreach (string protocol in protocols)
        {
            try
            {
                int result = await proxyProbe.GetStatusAsync(server, protocol, ct);
                if (result != server.ExpectedStatus)
                    return new(false, $"{protocol}：目标返回 {result}，预期 {server.ExpectedStatus}；请检查检测网站或代理出口。");
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                string errorMessage = DiagnosticLogger.Redact(error.ToString());
                var state = SshFailure.Classify(errorMessage);
                string detail = state == ConnectionState.AuthenticationFailed || state == ConnectionState.HostKeyError
                    ? "健康检测 SSH 会话失败：" + errorMessage
                    : errorMessage.Contains("administratively prohibited", StringComparison.OrdinalIgnoreCase) ? "服务器禁止 SSH 检测通道（direct-tcpip），未能验证代理出口。"
                    : "经 SSH 访问远程代理失败（可能是目标、出口或 SSH）；" + errorMessage;
                return new(false, detail);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { return new(false, "远程检测超时；无法确认是检测目标、代理出口还是 SSH 网络故障，请检查日志。"); }
        }
        return new(true, $"经 Linux 回环端口的 {string.Join(" / ", protocols)} 实际访问返回 {server.ExpectedStatus}；最近检查 {DateTime.Now:HH:mm:ss}");
    }
}

public sealed class OpenSshBackend(IRemoteTransport remote, IDiagnosticLogger logger) : IConnectionBackend
{
    private readonly ProxyHealthChecker checker = new(remote);
    public Task<bool> IsLocalReadyAsync(ServerProfile server, CancellationToken ct) => ProxyHealthChecker.IsLocalReadyAsync(server, ct);
    public Task<HealthResult> CheckAsync(ServerProfile server, CancellationToken ct) => checker.CheckAsync(server, ct);
    public async Task<ITunnelSession> StartAsync(ServerProfile server, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var config = await new SshConfigReader(new ProcessRunner()).ResolveAsync(server, ct);
        config.EnsureDedicatedTunnel();
        return new SshTunnelSession(server, logger, ct);
    }
}

internal sealed class SshTunnelSession : ITunnelSession
{
    private readonly Process process;
    private readonly ProcessJob? job;
    private readonly CancellationTokenRegistration cancellation;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder errors = new();
    private readonly object sync = new();
    public Task Ready => ready.Task;
    public Task Completion { get; }
    public string Error { get { lock (sync) return errors.ToString(); } }

    public SshTunnelSession(ServerProfile server, IDiagnosticLogger logger, CancellationToken ct)
    {
        var start = SshCommand.Create(server);
        foreach (var arg in new[] { "-N", "-T", "-v", "-o", "ExitOnForwardFailure=yes", "-R", $"127.0.0.1:{server.RemotePort}:127.0.0.1:{server.LocalPort}" }) start.ArgumentList.Add(arg);
        SshCommand.AddDestination(start, server.Host);
        process = new Process { StartInfo = start };
        process.Start();
        try { job = ProcessJob.Attach(process); }
        catch { SshCommand.Kill(process); process.Dispose(); throw; }
        process.StandardInput.Close();
        cancellation = ct.Register(() => SshCommand.Kill(process));
        Completion = ObserveAsync(server, logger);
    }

    private async Task ObserveAsync(ServerProfile server, IDiagnosticLogger logger)
    {
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                // OpenSSH prints this from "remote forward %s for: listen %s%s%d"; the success words are not a single binary string.
                if (line.Contains("remote forward success for:", StringComparison.Ordinal) && line.Contains($"127.0.0.1:{server.RemotePort}", StringComparison.Ordinal))
                    ready.TrySetResult();
                var safe = DiagnosticLogger.Redact(line);
                lock (sync)
                {
                    if (errors.Length > 8000) errors.Remove(0, errors.Length - 4000);
                    errors.AppendLine(safe);
                }
                // Verbose output stays in memory for classification; persist only errors.
                if (!line.StartsWith("debug", StringComparison.OrdinalIgnoreCase)) logger.Write(server.Name, safe);
            }
        }
        await Task.WhenAll(ReadAsync(process.StandardError), ReadAsync(process.StandardOutput), process.WaitForExitAsync());
        // Completion is also observed while waiting for Ready; no unobserved fault.
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Dispose();
        SshCommand.Kill(process);
        try { await Completion.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
        job?.Dispose();
        process.Dispose();
    }
}

public sealed class SshTunnelManager : IAsyncDisposable
{
    private readonly IConnectionBackend backend;
    private readonly IDiagnosticLogger logger;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(1);
    private CancellationTokenSource? stop;
    private Task? worker;
    private readonly TimeSpan checkInterval;
    public ServerProfile Server { get; }
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Create(ConnectionState.Paused, "尚未连接");
    public event Action<ConnectionStatus>? Changed;
    public bool IsRunning => stop is { IsCancellationRequested: false } && worker is { IsCompleted: false };

    public SshTunnelManager(ServerProfile server, IConnectionBackend backend, IDiagnosticLogger logger, TimeSpan? checkInterval = null)
    {
        Server = server; this.backend = backend; this.logger = logger;
        this.checkInterval = checkInterval ?? TimeSpan.FromSeconds(30);
    }
    private void Set(ConnectionState state, string detail)
    {
        Status = ConnectionStatus.Create(state, detail);
        logger.Write(Server.Name, Status.Label + ": " + detail);
        Changed?.Invoke(Status);
    }
    public async Task StartAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (IsRunning) return;
            Server.Validate();
            stop?.Dispose();
            stop = new CancellationTokenSource();
            worker = Task.Run(() => RunAsync(stop.Token));
        }
        finally { lifecycle.Release(); }
    }
    public async Task PauseAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (stop is not null) await stop.CancelAsync();
            if (worker is not null) await worker;
            Set(ConnectionState.Paused, "用户已暂停，自动重连已停止。");
        }
        finally { lifecycle.Release(); }
    }
    public void Wake() => wake.Writer.TryWrite(true);
    public async Task ReconnectAsync() { await PauseAsync(); await StartAsync(); }
    private async Task DelayAsync(TimeSpan duration, CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(duration);
        try { await wake.Reader.ReadAsync(timer.Token); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await backend.IsLocalReadyAsync(Server, ct))
                {
                    Set(ConnectionState.WaitingForProxy, $"Windows 127.0.0.1:{Server.LocalPort} 未就绪；等待代理启动。");
                    await DelayAsync(TimeSpan.FromSeconds(5), ct);
                    continue;
                }
                Set(ConnectionState.Connecting, "正在建立 SSH 反向转发。");
                await using var session = await backend.StartAsync(Server, ct);
                var deadline = Task.Delay(TimeSpan.FromSeconds(25), ct);
                var first = await Task.WhenAny(session.Ready, session.Completion, deadline);
                ct.ThrowIfCancellationRequested();
                if (first != session.Ready) throw new IOException(session.Error.Length > 0 ? session.Error : "SSH 转发建立超时。");
                await session.Ready;
                Set(ConnectionState.SshConnected, $"远程 127.0.0.1:{Server.RemotePort} → 本地 :{Server.LocalPort}");
                while (!session.Completion.IsCompleted)
                {
                    if (!await backend.IsLocalReadyAsync(Server, ct)) break;
                    Set(ConnectionState.CheckingProxy, "SSH 转发已确认，正在从 Linux 检测实际访问。");
                    using var healthStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var health = backend.CheckAsync(Server, healthStop.Token);
                    var healthFirst = await Task.WhenAny(health, session.Completion);
                    if (healthFirst == session.Completion)
                    {
                        await healthStop.CancelAsync();
                        try { await health; } catch (OperationCanceledException) { }
                        throw new IOException(session.Error);
                    }
                    var result = await health;
                    ct.ThrowIfCancellationRequested();
                    if (session.Completion.IsCompleted) throw new IOException(session.Error);
                    if (result.Fatal) { Set(ConnectionState.Error, result.Detail); return; }
                    Set(result.Healthy ? ConnectionState.Healthy : ConnectionState.ProxyError, result.Detail);
                    if (result.Healthy) attempt = 0;
                    using var idleStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var delay = DelayAsync(checkInterval, idleStop.Token);
                    await Task.WhenAny(delay, session.Completion);
                    if (session.Completion.IsCompleted)
                    {
                        await idleStop.CancelAsync();
                        try { await delay; } catch (OperationCanceledException) { }
                        throw new IOException(session.Error);
                    }
                    await delay;
                }
                if (session.Completion.IsCompleted) throw new IOException(session.Error);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            {
                // Configuration mistakes do not heal by reconnecting. Keep the reason visible until the user starts again.
                Set(ConnectionState.Error, DiagnosticLogger.Redact(error.Message));
                return;
            }
            catch (Exception error)
            {
                var state = SshFailure.Classify(error.Message);
                string detail = state == ConnectionState.PortConflict ? "远程端口不可用，占用者未知。请更换端口或自行关闭旧连接。" : DiagnosticLogger.Redact(error.Message);
                Set(state, detail);
                try
                {
                    var delay = SshFailure.RetryDelay(attempt++);
                    // Keep authentication, host-key and port errors visible throughout the backoff.
                    if (Status.State is not (ConnectionState.AuthenticationFailed or ConnectionState.HostKeyError or ConnectionState.PortConflict))
                    {
                        string reason = detail.Length > 240 ? detail[..240] : detail;
                        Set(ConnectionState.Reconnecting, $"{delay.TotalSeconds:0} 秒后重试。上次失败：{reason}");
                    }
                    await DelayAsync(delay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
    }
    public async ValueTask DisposeAsync() { await PauseAsync(); stop?.Dispose(); }
}
