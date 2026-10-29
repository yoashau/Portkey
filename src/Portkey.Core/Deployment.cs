using System.Text.Json;
using System.Text.RegularExpressions;

namespace Portkey.Core;

public sealed record RemoteFile(string? Content, string? Hash, int Mode = 384);
public sealed record FileChange(string Path, string? BeforeHash, string? Content, int? Mode = null);
public sealed record BackupInfo(string Id, List<FileChange> Restore);
public sealed record RemoteSnapshot
{
    public string User { get; init; } = "";
    public string Home { get; init; } = "";
    public int Uid { get; init; }
    public string Shell { get; init; } = "";
    public string Os { get; init; } = "";
    public bool Systemd { get; init; }
    public string SystemdEnvironment { get; init; } = "";
    public string Listeners { get; init; } = "";
    public Dictionary<string, RemoteFile> Files { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<BackupInfo> Backups { get; init; } = [];
    public string ShellEnvironment { get; init; } = "";
}

public sealed record DeploymentPlan(string Operation, List<FileChange> Changes, List<string> Warnings, bool Systemd)
{
    public string Preview(RemoteSnapshot snapshot)
    {
        var lines = new List<string> { $"用户：{snapshot.User}  HOME：{snapshot.Home}", $"系统：{snapshot.Os}  Shell：{snapshot.Shell}", $"操作：{Operation}", "" };
        lines.AddRange(Warnings.Select(x => "提示：" + x));
        foreach (var change in Changes)
        {
            lines.Add($"\n{(change.Content is null ? "删除" : change.BeforeHash is null ? "新增" : "修改")} ~/{change.Path}");
            lines.Add("--- 当前内容");
            lines.Add(snapshot.Files[change.Path].Content ?? "（不存在）");
            lines.Add("+++ 应用后内容");
            lines.Add(change.Content ?? "（删除此文件）");
        }
        if (Changes.Count == 0) lines.Add("配置已符合预期，无文件需要修改。");
        lines.Add("\n现有进程不会自动继承新环境。标准代理变量只影响遵守这些变量的程序。");
        return string.Join('\n', lines);
    }
}

public sealed record OwnershipManifest
{
    public int Version { get; init; } = 1;
    public Dictionary<string, string> Files { get; init; } = [];
    public Dictionary<string, string> Blocks { get; init; } = [];
    public HashSet<string> PrefixNewlines { get; init; } = [];
    public HashSet<string> CreatedShellFiles { get; init; } = [];
}

public static class LinuxDeploymentPlanner
{
    public const string Begin = "# >>> Portkey managed configuration >>>";
    public const string End = "# <<< Portkey managed configuration <<<";
    public const string ManifestPath = ".config/portkey/manifest.json";
    public static bool VerifiesCurrentProfile(string operation) => operation.StartsWith("安装", StringComparison.Ordinal);
    public const string ShellPath = ".config/portkey/proxy.sh";
    public const string SystemdPath = ".config/environment.d/90-portkey.conf";
    public static readonly string[] ShellFiles = [".bashrc", ".profile", ".bash_profile", ".bash_login"];

    public static DeploymentPlan Create(RemoteSnapshot snapshot, ServerProfile server, bool uninstall = false, bool allowEdited = false)
    {
        server.Validate();
        if (snapshot.Uid == 0) throw new InvalidOperationException("请使用目标普通用户部署，不能使用 root。");
        if (!snapshot.Shell.EndsWith("/bash", StringComparison.Ordinal)) throw new InvalidOperationException("第一版仅支持 Bash；未修改远程文件。");
        if (!Regex.IsMatch(snapshot.Os, @"(?im)^ID=(""?)(ubuntu|debian)\1$")) throw new InvalidOperationException("第一版仅在 Ubuntu / Debian 上提供自动部署。");
        var manifestText = snapshot.Files[ManifestPath].Content;
        var previous = manifestText is null ? new OwnershipManifest() : JsonSerializer.Deserialize<OwnershipManifest>(manifestText, DataFormat.Json)
            ?? throw new InvalidOperationException("Portkey 清单损坏，请先恢复备份。");
        if (previous.Version != 1) throw new InvalidOperationException("不支持此 Portkey 配置版本，请使用对应版本恢复或迁移。");
        if (previous.Files.Keys.Any(x => x != ShellPath && x != SystemdPath) || previous.Blocks.Keys.Any(x => !ShellFiles.Contains(x)))
            throw new InvalidOperationException("Portkey 清单包含不允许管理的路径。");

        var warnings = new List<string>(snapshot.Warnings);
        warnings.Add(snapshot.Systemd ? "systemd 用户管理器可用；提交后重新加载并验证。" : "systemd 用户管理器不可用，仅覆盖新启动的 Bash 环境。");
        warnings.Add("127.0.0.1 避免向外网开放，但同机其他用户仍可能访问此 TCP 端口。");
        if (Regex.IsMatch(snapshot.Listeners, @":" + server.RemotePort + @"\s"))
            warnings.Add($"远程 :{server.RemotePort} 已有监听，可能是当前 Portkey 或其他隧道。占用者未知；不会关闭其他连接。连接失败时请更换端口或手动处理。");
        foreach (var path in ShellFiles.Concat(snapshot.Files.Keys.Where(x => x.EndsWith(".conf") && x != SystemdPath)).Distinct())
        {
            var text = snapshot.Files.GetValueOrDefault(path)?.Content;
            if (text is not null && Regex.IsMatch(RemoveBlock(text, out _), @"(?im)^[^#\r\n]*(http_proxy|https_proxy|all_proxy|no_proxy)\s*="))
                warnings.Add($"~/{path} 存在其他代理赋值；会保留，请检查优先级，必要时手动处理。");
        }
        var desired = new Dictionary<string, string?>();
        var next = new OwnershipManifest();
        var values = Variables(server);
        var shell = "# Portkey managed v1\n" + string.Join('\n', values.Select(x => $"export {x.Key}={DataFormat.ShellQuote(x.Value)}")) + "\n";
        var environment = "# Portkey managed v1\n" + string.Join('\n', values.Select(x => $"{x.Key}={x.Value}")) + "\n";

        foreach (var path in new[] { ShellPath, SystemdPath })
        {
            var current = snapshot.Files[path];
            if (current.Content is not null)
            {
                if (!previous.Files.TryGetValue(path, out var digest)) throw new InvalidOperationException($"~/{path} 已存在但没有可信 Portkey 所有权记录；请手动检查，未覆盖。");
                if (current.Hash != digest && !allowEdited) throw new InvalidOperationException($"~/{path} 被手动编辑。请审阅内容，再显式允许替换已编辑的 Portkey 内容。");
            }
            var content = uninstall ? null : path == ShellPath ? shell : snapshot.Systemd ? environment : null;
            // Keep an existing owned environment.d file when systemd is temporarily offline.
            if (!uninstall && path == SystemdPath && !snapshot.Systemd && current.Content is not null) content = environment;
            desired[path] = content;
            if (content is not null) next.Files[path] = DataFormat.Hash(content);
        }

        string login = new[] { ".bash_profile", ".bash_login", ".profile" }.FirstOrDefault(x => snapshot.Files[x].Content is not null) ?? ".profile";
        foreach (var path in ShellFiles)
        {
            var original = snapshot.Files[path].Content;
            var clean = RemoveBlock(original ?? "", out var block, previous.PrefixNewlines.Contains(path));
            if (block is not null)
            {
                if (!previous.Blocks.TryGetValue(path, out var hash)) throw new InvalidOperationException($"~/{path} 的代码块缺少所有权记录；请手动审阅。");
                if (DataFormat.Hash(block) != hash && !allowEdited) throw new InvalidOperationException($"~/{path} 的 Portkey 代码块已被编辑；请显式允许替换后再预览。");
            }
            if (!uninstall && (path == ".bashrc" || path == login))
            {
                // Preserve existing bytes, but our executable Bash lines need LF:
                // CRLF after `then`/`fi` is not valid Bash syntax.
                const string newline = "\n";
                var newBlock = string.Join(newline, new[] { Begin, "if [ -n \"${BASH_VERSION-}\" ]; then", "    case $- in", "        *i*) [ ! -r \"$HOME/.config/portkey/proxy.sh\" ] || . \"$HOME/.config/portkey/proxy.sh\" ;;", "        *) if shopt -q login_shell; then", "               [ ! -r \"$HOME/.config/portkey/proxy.sh\" ] || . \"$HOME/.config/portkey/proxy.sh\"", "           fi ;;", "    esac", "fi", End });
                // Insert a leading newline inside our owned block, so uninstall preserves original bytes.
                var prefix = block is null && clean.Length > 0 && !clean.EndsWith('\n') ? newline : "";
                if (block is not null)
                {
                    desired[path] = (original ?? "").Replace(block, newBlock, StringComparison.Ordinal);
                    next.Blocks[path] = DataFormat.Hash(newBlock);
                    if (previous.PrefixNewlines.Contains(path)) next.PrefixNewlines.Add(path);
                }
                else
                {
                    desired[path] = clean + prefix + newBlock + newline;
                    next.Blocks[path] = DataFormat.Hash(newBlock);
                    if (prefix.Length > 0) next.PrefixNewlines.Add(path);
                }
                if (original is null || previous.CreatedShellFiles.Contains(path)) next.CreatedShellFiles.Add(path);
            }
            else if (block is not null) desired[path] = clean.Length == 0 && previous.CreatedShellFiles.Contains(path) ? null : clean;
        }
        desired[ManifestPath] = uninstall ? null : JsonSerializer.Serialize(next, DataFormat.Json) + "\n";
        var changes = desired.Where(x => x.Value != snapshot.Files[x.Key].Content)
            .Select(x => new FileChange(x.Key, snapshot.Files[x.Key].Hash, x.Value)).ToList();
        return new DeploymentPlan(uninstall ? "卸载 Portkey 用户配置" : "安装 / 修复 Portkey 用户配置", changes, warnings, snapshot.Systemd);
    }

    public static Dictionary<string, string> Variables(ServerProfile server)
    {
        var http = $"{(server.Protocol == ProxyProtocol.Socks5 ? "socks5h" : "http")}://127.0.0.1:{server.RemotePort}";
        var all = $"{(server.Protocol == ProxyProtocol.Http ? "http" : "socks5h")}://127.0.0.1:{server.RemotePort}";
        return new() { ["http_proxy"] = http, ["https_proxy"] = http, ["all_proxy"] = all, ["HTTP_PROXY"] = http, ["HTTPS_PROXY"] = http, ["ALL_PROXY"] = all, ["no_proxy"] = "localhost,127.0.0.1,::1", ["NO_PROXY"] = "localhost,127.0.0.1,::1" };
    }

    public static string RemoveBlock(string text, out string? block, bool removeSeparator = false)
    {
        block = null;
        int begin = text.IndexOf(Begin, StringComparison.Ordinal), end = text.IndexOf(End, StringComparison.Ordinal);
        if (begin < 0 && end < 0) return text;
        if (begin < 0 || end < begin || (begin > 0 && text[begin - 1] != '\n') || text.IndexOf(Begin, begin + Begin.Length, StringComparison.Ordinal) >= 0 || text.IndexOf(End, end + End.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("发现不完整或重复的 Portkey 标记；为保护用户内容，请手动修复标记或恢复备份。");
        int after = end + End.Length;
        if (after < text.Length && text[after] != '\r' && text[after] != '\n') throw new InvalidOperationException("Portkey 结束标记所在行被编辑。");
        block = text[begin..after];
        if (removeSeparator && begin > 0 && text[begin - 1] == '\n')
        {
            begin--;
        }
        if (after < text.Length && text[after] == '\r') after++;
        if (after < text.Length && text[after] == '\n') after++;
        return text[..begin] + text[after..];
    }
}
