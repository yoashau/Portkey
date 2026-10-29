# 运维与故障处理

## 代码路径

| 位置 | 职责 |
| --- | --- |
| `Portkey.Core/Models.cs` | 配置验证、状态、错误分类、退避 |
| `Portkey.Core/Deployment.cs` | 纯函数部署规划、内容所有权、预览 |
| `Portkey.Infrastructure/Ssh.cs` | OpenSSH 参数、`ssh -G`、Host 候选发现、超时进程执行 |
| `Portkey.Infrastructure/Connections.cs` | SSH 生命周期、转发确认、代理探测、重试/取消 |
| `Portkey.Infrastructure/SshProxyProbe.cs` | 经 SSH stdio 的远程端口探测，复用 .NET HTTP/SOCKS5/TLS 实现 |
| `Portkey.Infrastructure/ProcessJob.cs` | Windows Job Object，进程异常退出时结束自有子进程 |
| `Portkey.Infrastructure/RemoteEnvironment.cs` | 只读检查与已确认计划提交、提交后验证 |
| `Portkey.Infrastructure/Remote/agent.py` | 远程固定路径文件事务、备份、回滚、锁 |
| `Portkey.Infrastructure/Storage.cs` | 配置原子读写、异步有界日志与轮转 |
| `Portkey.App` | WPF 面板/页面、托盘 Win32 外壳、VM、系统事件、自启动 |

不使用 DI 容器或外部 MVVM 框架。远程脚本作为嵌入资源通过 SSH stdin 传输，不需要在 Linux 安装 Portkey 守护进程。请求使用 Base64 编码 JSON 单个安全参数；配置字段从不作为裸 Shell 命令拼接。进程参数使用 `ArgumentList`。

SSH 连接状态依次经过本地回环端口可用、OpenSSH `remote forward success`、经 Linux 回环端口的实际 HTTPS 请求。Mixed 模式两个协议都通过才为正常。30 秒周期复查；SSH 退出立即触发重连，本地代理缺失进入等待。

健康检测使用 OpenSSH 自带的 `-W 127.0.0.1:远程端口`，将标准输入/输出作为远程 TCP 流交给 .NET `SocketsHttpHandler.ConnectCallback`。不创建 Windows 探测监听端口，不执行远程 HTTP 工具，也不安装软件。每个短期探测进程都由 Job Object 和取消机制管理。

```text
Windows .NET HTTP/SOCKS5 客户端
  → ssh -W → Linux 127.0.0.1:远程端口
  → 已有 SSH 反向隧道 → Windows 127.0.0.1:本地代理端口
  → 代理出口 → HTTPS 检测目标
```

必须经过 Linux 的监听端口才可能通过；真实负例测试已验证移除该监听后，即使 Windows 代理仍正常也不能通过。TLS 使用 Windows/.NET 信任库，不等于验证 Linux 程序自身的证书配置。若服务器禁止 direct-tcpip，探测会显示无法验证，不自动修改 sshd 配置。

每台连接有一个串行生命周期锁、取消源和后台循环。网络事件仅唤醒活跃连接；暂停后的连接不会因网络事件重新启动。工作进程和健康检测均受取消/超时约束。

## 典型问题

| 状态 / 错误 | 处理 |
| --- | --- |
| 找不到 ssh.exe | 通过 Windows 可选功能安装 OpenSSH Client；程序使用 System32/OpenSSH 的绝对路径 |
| Host key verification failed | 首次认证窗口中核对指纹后确认；已更改指纹需先联系管理员，不自动移除 known_hosts |
| Permission denied (publickey) | 检查目标用户、IdentityFile、公钥授权和 ssh-agent；应用不保存密码 |
| 等待本地代理 | 启动本地 FlClash，核对监听地址和端口；不必打开局域网访问 |
| remote port forwarding failed | 端口占用或服务器禁止转发；占用者未知，更换远程端口或自行关闭旧隧道 |
| SSH Host 已有转发 | 用专用 Host 别名继承连接信息，但不配置 LocalForward/RemoteForward/DynamicForward；不要让此工具重复启动其他业务隧道 |
| curl 不存在 | 不影响健康检测，不需要安装 curl |
| SSH 检测通道被禁止 | 服务端禁止 direct-tcpip；需管理员确认转发策略，不自动修改配置 |
| 远程代理连接失败 | 核对 Linux 监听、反向隧道、Windows 本地代理及 SSH 转发权限 |
| TLS 证书错误 | 检查检测目标、Windows CA 和时间，不跳过证书校验 |
| 状态码不是预期值 | 检查 URL 是否改变、出口策略、目标故障；可更换检测目标，不能据此断言 SSH 已断 |
| 超时 | 检测目标、代理出口或 SSH 网络均有可能。界面保留不确定性，不伪造根因 |
| systemd 不可用 | 仍可部署 Bash 配置；后台用户服务环境没有覆盖 |
| systemd 环境不同 | 检查更晚排序的 environment.d 文件、系统级环境来源和自定义 XDG_CONFIG_HOME |
| shell 环境不同 | 检查 `.bash_profile` / `.bash_login` 优先级、提前 exit/return 或后续变量覆盖；不自动清理用户逻辑 |
| 文件在预览后变化 | 重新生成预览；不复用旧差异强行覆盖 |
| 不完整 Portkey 标记 | 手动检查边界，或恢复备份。没有可靠边界时不能自动删除未知内容 |
| 中断事务待恢复 | 勾选确认并执行恢复中断事务；如果存在新用户编辑，停止并人工审阅 |

## 事务与边界

先校验文件摘要及所有权，再在 `~/.local/state/portkey/backups/` 保存完整旧内容/权限；持久化事务日志后才逐文件写入。同目录临时文件 + fsync + replace 保证单个文件原子替换。发生异常时从备份反向恢复；SSH 中断或强制终止后保留日志，下一次检查只读报错，由用户确认恢复。

写入操作以目标用户和 HOME 为键使用 flock，第二次并发写入立即失败。所有预览文件的摘要在提交前重新校验，每次替换前再次校验。锁只能协调 Portkey；同用户其他编辑器不遵守此锁，因此无法消除校验与 rename 之间极短的竞争窗口。不要在提交期间同时编辑这些文件。文件权限与 UTF-8 换行字节保留；不支持硬链接语义、ACL/xattr 保留或非 UTF-8 的自动修改。

多文件变更不是文件系统级整体原子操作，事务日志用于恢复中间状态。环境验证可能发现已有用户脚本覆盖，文件提交成功后会明确报告验证不通过并保留备份，供用户审阅/恢复；不会声称所有现存进程已经更新，也不为强行验证而清空其他变量。

systemd 文件同步和进程环境不是同一个事务。卸载时仅清除仍与 Portkey 原值匹配且没有其他用户 environment.d 赋值的用户管理器变量；管理员级环境来源完全一致时无法判断来源，需人工审阅。已运行服务的环境仍需重启后才变化。

备份内容可能包含原有 Shell 文件中的个人信息，保存在目标用户权限目录内，不上传、不写入 Windows 日志。保留最多 20 个事务备份；UI 默认提供最近一个成功提交的恢复预览，任何内容不符均拒绝恢复。

## 发布

`scripts/build.ps1` 使用全新 Windows 本地工作副本，成功后只将 EXE 同步到仓库 `artifacts/win-x64/` 并清理此次工作副本。失败时保留副本便于诊断。SDK 安装在用户目录，不需要更改系统 SDK 或管理员权限。

本项目使用 .NET 内置 Fluent ThemeMode，支持主题跟随；WindowChrome / DWM 圆角在 Windows 11 使用，旧版本采用普通窗口背景降级。实际 DPI/多屏/任务栏边缘组合需按验收矩阵测试。Aulay 的代码未被复制，不引入其蓝牙业务。

参考：[WPF Fluent 主题与 ThemeMode](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90)、[OpenSSH ssh(1)](https://man.openbsd.org/ssh.1)、[systemd environment.d](https://www.freedesktop.org/software/systemd/man/latest/environment.d.html)。
