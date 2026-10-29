using System.Reflection;
using System.Text;
using System.Text.Json;
using Portkey.Core;

namespace Portkey.Infrastructure;

public sealed class RemoteEnvironmentInspector(IRemoteTransport remote)
{
    private static readonly string Agent = ReadAgent();
    private static string ReadAgent()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Portkey.Infrastructure.Remote.agent.py")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    internal async Task<string> RequestAsync(ServerProfile server, object request, CancellationToken ct)
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, DataFormat.Json)));
        return await remote.RunAsync(server, "python3 - " + DataFormat.ShellQuote(encoded), Agent, TimeSpan.FromSeconds(90), ct);
    }
    public async Task<RemoteSnapshot> InspectAsync(ServerProfile server, CancellationToken ct)
    {
        var result = await RequestAsync(server, new { operation = "inspect" }, ct);
        return JsonSerializer.Deserialize<RemoteSnapshot>(result, DataFormat.Json) ?? throw new InvalidDataException("远程检查返回了无效结果。");
    }
    public Task<string> RecoverAsync(ServerProfile server, CancellationToken ct) => RequestAsync(server, new { operation = "recover" }, ct);
}

public sealed class LinuxDeploymentExecutor(RemoteEnvironmentInspector inspector, IRemoteTransport remote)
{
    public async Task<string> ApplyAsync(ServerProfile server, RemoteSnapshot snapshot, DeploymentPlan plan, CancellationToken ct)
    {
        var result = await inspector.RequestAsync(server, new { operation = "apply", changes = plan.Changes,
            expected = snapshot.Files.ToDictionary(x => x.Key, x => x.Value.Hash) }, ct);
        var verified = await inspector.InspectAsync(server, ct);
        foreach (var change in plan.Changes)
            if (verified.Files[change.Path].Content != change.Content) throw new IOException("提交后文件不符合预览；请检查备份与并发修改。");
        var report = new StringBuilder("文件已提交并校验。\n" + result);
        try
        {
            if (snapshot.Systemd)
            {
                var environment = await remote.RunAsync(server, "systemctl --user show-environment", null, TimeSpan.FromSeconds(15), ct);
                var entries = environment.Split('\n').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
                if (plan.Operation.StartsWith("卸载", StringComparison.Ordinal))
                {
                    report.AppendLine("\nsystemd 已尝试重新加载；只清除仍与 Portkey 原值相同且没有其他用户 environment.d 赋值的变量。其他来源保留。详细结果见上方。");
                }
                else if (LinuxDeploymentPlanner.VerifiesCurrentProfile(plan.Operation))
                {
                    var expected = LinuxDeploymentPlanner.Variables(server);
                    report.AppendLine(expected.All(x => entries.GetValueOrDefault(x.Key) == x.Value)
                        ? "\nsystemd 八项代理变量验证通过。" : "\nsystemd 环境与预期不同，可能被其他 environment.d 覆盖；文件已保留，请检查优先级。");
                }
            }
            if (LinuxDeploymentPlanner.VerifiesCurrentProfile(plan.Operation))
            {
                const string command = "bash -lc 'printf \"\\nPORTKEY_VERIFY=%s\\n\" \"${https_proxy-}\"'";
                var shell = await remote.RunAsync(server, command, null, TimeSpan.FromSeconds(20), ct);
                var expected = LinuxDeploymentPlanner.Variables(server)["https_proxy"];
                report.AppendLine(shell.Contains("PORTKEY_VERIFY=" + expected + "\n", StringComparison.Ordinal)
                    ? "Bash 登录环境验证通过。" : "Bash 登录环境未符合预期；请检查用户启动文件中的其他赋值或提前退出。");
            }
        }
        catch (IOException error) { report.AppendLine("环境验证失败：" + error.Message + "；文件备份已保留，可通过恢复备份撤销。"); }
        return report.ToString();
    }
}
