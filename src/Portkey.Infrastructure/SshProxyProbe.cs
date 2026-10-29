using System.Diagnostics;
using System.Net;
using System.Text;
using Portkey.Core;

namespace Portkey.Infrastructure;

public interface IProxyProbe
{
    Task<int> GetStatusAsync(ServerProfile server, string protocol, CancellationToken ct);
}

public sealed class SshProxyProbe : IProxyProbe
{
    public async Task<int> GetStatusAsync(ServerProfile server, string protocol, CancellationToken ct)
    {
        // The proxy connection is ssh -W's stdio stream. There is no Windows
        // TCP connection to the local proxy here: sshd must connect to the Linux
        // loopback listener, which traverses the reverse tunnel back to Windows.
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"{protocol}://127.0.0.1:{server.RemotePort}"),
            UseProxy = true,
            AllowAutoRedirect = false,
            ConnectCallback = (context, token) =>
            {
                if (context.DnsEndPoint.Host != "127.0.0.1" || context.DnsEndPoint.Port != server.RemotePort)
                    throw new IOException("健康检测不能绕过远程代理端口。");
                return ValueTask.FromResult<Stream>(new SshForwardStream(server, token));
            }
        };
        using var client = new HttpClient(handler);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(server.TimeoutSeconds));
        using var response = await client.GetAsync(server.CheckUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        return (int)response.StatusCode;
    }
}

internal sealed class SshForwardStream : Stream
{
    private readonly Process process;
    private readonly ProcessJob? job;
    private readonly Stream input, output;
    private readonly Task<string> errors;
    private readonly CancellationTokenRegistration cancellation;
    private int disposed;

    public SshForwardStream(ServerProfile server, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = CreateStartInfo(server);
        process = new Process { StartInfo = start };
        process.Start();
        try { job = ProcessJob.Attach(process); }
        catch { SshCommand.Kill(process); process.Dispose(); throw; }
        input = process.StandardInput.BaseStream;
        output = process.StandardOutput.BaseStream;
        errors = ReadErrorsAsync(process.StandardError);
        cancellation = ct.Register(() => SshCommand.Kill(process));
    }

    internal static ProcessStartInfo CreateStartInfo(ServerProfile server)
    {
        var start = SshCommand.Create(server);
        foreach (var arg in new[] { "-T", "-o", "ClearAllForwardings=yes", "-o", "ExitOnForwardFailure=yes", "-W", $"127.0.0.1:{server.RemotePort}" }) start.ArgumentList.Add(arg);
        SshCommand.AddDestination(start, server.Host);
        return start;
    }
    private static async Task<string> ReadErrorsAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (text.Length > 4000) text.Remove(0, text.Length - 2000);
                text.AppendLine(DiagnosticLogger.Redact(line));
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        return text.ToString();
    }

    public override bool CanRead => disposed == 0;
    public override bool CanWrite => disposed == 0;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => input.Flush();
    public override Task FlushAsync(CancellationToken ct) => input.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int count = await output.ReadAsync(buffer, ct);
        if (count == 0 && buffer.Length > 0)
        {
            try
            {
                string error = await errors.WaitAsync(TimeSpan.FromSeconds(1), ct);
                if (error.Length > 0) throw new IOException(error);
            }
            catch (TimeoutException) { }
        }
        return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => input.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => input.WriteAsync(buffer, ct);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => input.WriteAsync(buffer, offset, count, ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            cancellation.Dispose();
            SshCommand.Kill(process);
            job?.Dispose();
            process.WaitForExit(1000);
            process.Dispose();
        }
        base.Dispose(disposing);
    }
}
