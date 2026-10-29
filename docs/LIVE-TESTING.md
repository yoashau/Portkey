# 授权测试机的真实测试记录

2026-10-21，Asia/Shanghai。测试者明确授权连接一台自有服务器；首轮仅执行只读检查与临时端口转发。后续获得写入授权，已在同一账户完成持久化部署与桌面接管，见 [部署测试记录](DEPLOYMENT-TESTING.md)。以下保留首轮结果。主机别名、账号、地址和家目录不写入仓库，运行时通过 `-HostAlias` 指定。

## 实际环境

| 项目 | 检查结果 |
| --- | --- |
| 执行端 | Windows .NET 10 / Windows OpenSSH 9.5p2 |
| SSH 目标 | 由 `-HostAlias` 指定的已配置 Host，公钥认证 |
| 认证 | publickey，BatchMode=yes，StrictHostKeyChecking=yes |
| 系统 | Ubuntu 20.04.6 LTS，Bash，普通用户 |
| 本地代理 | Windows 127.0.0.1:7890，HTTP 与 SOCKS5 HTTPS 请求均返回 204 |
| 已有监听 | Linux 127.0.0.1:17890，未停止、替换或抢占 |
| 测试监听 | Linux 127.0.0.1:27890，开始前空闲，结束后释放 |
| 远程工具 | 无 curl；ss、Python 3 可用，systemd 用户管理器可用 |

## 发现和修复

旧版 SSH 反向转发实际成功，但健康检查依赖远程 curl，返回 `env: ‘curl’: No such file or directory`。因此改为不依赖另装 HTTP 工具：

```text
Windows .NET HTTP/SOCKS5 请求
  → ssh -W 127.0.0.1:27890
  → Linux 127.0.0.1:27890
  → Portkey 创建的反向隧道
  → Windows 127.0.0.1:7890
  → https://www.gstatic.com/generate_204
```

`SocketsHttpHandler.ConnectCallback` 强制代理 TCP 连接使用 SSH 的 stdio 流，HTTPS 校验和 HTTP/SOCKS5 协议交给 .NET。没有改成“只测 Windows 本地代理”，也没有在远端安装 curl、wget 或探测脚本。探测结束会清理自己创建的短期 SSH 进程。

这种检测确认完整网络链路，不能代替 Linux 应用自身的证书库/环境继承检查。服务器必须允许检测所用的 direct-tcpip 通道；禁止时显示无法验证，而不自动修改 sshd。

## 实测结果：11 / 11 通过

| 检查 | 实际结果 |
| --- | --- |
| SSH 配置与免密认证 | Windows 读取指定别名，实际登录用户匹配 |
| 用户环境检查和部署预览 | 只读检查成功，生成 5 处计划变更，未提交 |
| Windows HTTP/SOCKS5 | 两种协议均实际 204 |
| 真实反向转发 | OpenSSH 确认成功，ss 显示 127.0.0.1:27890 |
| 经 Linux 端口的出口 | HTTP/SOCKS5 均实际 204，管理器 Healthy |
| 真实端口占用 | 第二条相同端口隧道失败，第一条隧道保留 |
| 真实断线重连 | 只终止测试创建的 SSH；3 秒退避后重建，HTTP/SOCKS5 再次通过 |
| 主动暂停 | 临时监听释放，Wake 后不重连 |
| 不得绕过 Linux 端口 | 移除 27890 后 Windows 7890 仍正常，但探测失败，证明确实依赖远程路径 |
| 检测目标结果异常 | 配置预期 200，实际返回 204；SSH 仍连接，状态保持 ProxyError |
| 现有环境保护 | 测试前后所有已读用户配置摘要相同，原有监听保留 |

最后一次完整测试时间：19:27:29–19:27:46。原始日志保存在本地 `artifacts/live/live-test.log`，该目录不进入版本库。测试入口：

```powershell
.\scripts\test-live.ps1 -HostAlias <ssh-host> -RemotePort 27890
```

此命令必须显式调用，不在日常构建中自动运行。它不执行部署、systemd reload、修改启动项或终止非本次创建的 SSH。它会生成包含现有 Shell 内容的本地预览文件，请勿未经审阅上传分享。

## 后续真实写入已完成

测试者随后授权替换旧的手动配置与旧 17890 隧道。实际部署、幂等、新 Bash/systemd 用户服务环境、Linux HTTPS 204、暂停后配置保留与桌面程序接管均通过，临时备份在成功后删除。详见 [DEPLOYMENT-TESTING.md](DEPLOYMENT-TESTING.md)。包含旧启动文件内容的临时预览没有进入版本库。
