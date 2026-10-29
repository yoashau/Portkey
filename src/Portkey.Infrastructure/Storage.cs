using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Portkey.Core;

namespace Portkey.Infrastructure;

public sealed class SettingsStore(string directory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public string DirectoryPath { get; } = directory;
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(DirectoryPath, "settings.json");
        if (!File.Exists(path)) return new AppSettings();
        var info = new FileInfo(path);
        if (info.Length > 1024 * 1024) throw new InvalidDataException("配置文件过大。");
        var settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(path, ct), DataFormat.Json)
            ?? throw new InvalidDataException("配置为空；请从 settings.backup.json 恢复。");
        // Version 1 is the initial schema; future schemas are never silently downgraded.
        if (settings.Version != 1) throw new InvalidDataException("不支持此配置版本。请使用对应版本，原文件未改动。");
        if (settings.Servers.Select(x => x.Id).Distinct().Count() != settings.Servers.Count) throw new InvalidDataException("服务器 ID 重复。");
        foreach (var server in settings.Servers) server.Validate();
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        foreach (var server in settings.Servers) server.Validate();
        await gate.WaitAsync(ct);
        string temporary = Path.Combine(DirectoryPath, Guid.NewGuid() + ".tmp");
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings, DataFormat.Json));
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await file.WriteAsync(data, ct);
                await file.FlushAsync(ct);
                file.Flush(true);
            }
            var destination = Path.Combine(DirectoryPath, "settings.json");
            if (File.Exists(destination)) File.Replace(temporary, destination, Path.Combine(DirectoryPath, "settings.backup.json"));
            else File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            gate.Release();
        }
    }
}

public sealed class DiagnosticLogger : IDiagnosticLogger, IAsyncDisposable
{
    private readonly Channel<string> queue = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task writer;
    public string DirectoryPath { get; }
    public event Action<string>? Written;
    public string? StorageError { get; private set; }
    public DiagnosticLogger(string directory)
    {
        DirectoryPath = directory;
        writer = Task.Run(WriteLoopAsync);
    }
    public static string Redact(string message)
    {
        message = Regex.Replace(message, @"(?is)-----BEGIN [^-]*PRIVATE KEY-----.*", "[private key removed]");
        message = Regex.Replace(message, @"(?i)(https?://)[^\s/@]+:[^\s/@]+@", "$1[credentials]@");
        message = Regex.Replace(message, @"(?i)(password|passphrase|token|authorization)\s*[:=]\s*\S+", "$1=[redacted]");
        message = Regex.Replace(message, @"(?<![A-Za-z])[A-Za-z]:[\\/][^\r\n'"" ]+", "[local path]");
        return message.Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(1500, message.Length)];
    }
    public void Write(string category, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{category}] {Redact(message)}";
        queue.Writer.TryWrite(line);
        Written?.Invoke(line);
    }
    private async Task WriteLoopAsync()
    {
        await foreach (var line in queue.Reader.ReadAllAsync())
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string path = Path.Combine(DirectoryPath, "portkey.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                {
                    for (int index = 2; index >= 0; index--)
                    {
                        string source = index == 0 ? path : path + "." + index;
                        if (File.Exists(source)) File.Move(source, path + "." + (index + 1), true);
                    }
                }
                await File.AppendAllTextAsync(path, line + Environment.NewLine);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { StorageError = error.Message; }
        }
    }
    public async ValueTask DisposeAsync() { queue.Writer.TryComplete(); await writer; }
}
