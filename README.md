# Portkey

Windows 10/11 x64 托盘式 SSH 反向代理管理器。C# / .NET 10 / WPF，使用 Windows 自带 OpenSSH；没有 Electron、浏览器运行时、常驻 PowerShell 或任务计划程序。

应用图标与托盘图标统一采用系统字体 Link 字形的斜向链条轮廓，共用 `TrayIcon.cs` 的绘制实现。应用 ICO 包含 16–256 像素尺寸，并提供浅色版本；用户原图保存在 `assets/Portkey.original.png`。在 Windows 运行 `scripts/make-icons.ps1` 可重新生成资产，无需 Pillow。

## 获取和启动

Windows x64 自包含单文件发布包：

[Portkey.exe](https://github.com/yoashau/Portkey/releases/download/v1.0.0/Portkey.exe)

也可以在 Windows 上运行 `scripts/build.ps1`，产物位于 `artifacts/win-x64/Portkey.exe`。该目录不进入版本库。将 EXE 复制到固定目录后运行。自包含单文件，无需另装 .NET Runtime；首次启动会解压原生运行库到 .NET 的用户缓存。EXE 约 165 MiB，这主要来自自包含的 WPF/.NET 运行库。无需管理员权限。当前产物未签名。

启动后只显示托盘图标。左键展开/收起，点击外部或外部窗口获得焦点时收起，Esc 收起；右键提供显示、连接/暂停、重连、日志和退出。第二次启动会激活已有实例。可用 `Portkey.exe --show` 在启动时显示面板。

托盘图标不叠加状态圆点，随任务栏主题切换明暗；悬停提示与面板展示实际连接状态。面板可切换各服务器，后台独立维持多条连接。

## 第一次使用

1. 启动 FlClash / Clash 等本地代理，确认监听回环地址与端口。通常是混合端口 `7890`，不必开启局域网访问。
2. 点击托盘 →「连接设置 / 添加服务器」。从 Windows `%USERPROFILE%\.ssh\config` 的 Host 列表导入，或输入域名/IP。用户名、SSH 端口、密钥路径留空时遵循 OpenSSH 配置。
3. 设置本地/远程端口和 HTTP、SOCKS5 或 Mixed 协议。远程端口必须为 1024–65535，默认 `17890`。检测 URL 必须是 HTTPS，可设置预期状态码和超时。
4. 可点击「检测本地 HTTP / SOCKS5 代理」，通过真实 HTTPS 请求确认协议。失败也可能由检测目标或出口故障造成，不能据此直接判定端口不支持该协议。
5. 新主机先向管理员核对 SHA256 指纹，再点击「打开首次 SSH 认证窗口」，在 Windows OpenSSH 控制台中完成确认。Portkey 不自动接受未知或改变的主机密钥。加密私钥应事先通过 Windows `ssh-add` 加入可用的 ssh-agent；后台不等待密码或口令输入。
6. 保存配置，回到首页点击「连接 / 暂停」。程序建立 `-R 127.0.0.1:远程端口:127.0.0.1:本地端口`，确认 OpenSSH 转发成功后，通过 `ssh -W` 连接 Linux 的该回环端口，再由 Windows .NET 经此通道发起实际 HTTPS 请求。Mixed 模式分别验证 HTTP 与 SOCKS5，域名经代理解析。无需在服务器安装 curl。
7. 如果希望 Linux 新启动的程序默认使用代理，继续下面的用户环境部署。仅使用隧道时，不必部署 Shell 环境。

## 配置全新 Linux 普通用户

第一版自动配置支持 Ubuntu / Debian、Bash、Python 3。健康探测只使用已有 SSH 通道和 Windows .NET HTTP/SOCKS5 客户端；监听边界检查使用 Ubuntu 的 `ss`。curl/wget 不是要求，也不需要 Python 来发送健康请求。Python 3 仅用于用户环境检查/配置事务。无需 sudo，不安装 Linux 代理内核。裁剪镜像缺少 ss 或配置所需工具时明确报错。

1. 选择服务器 →「服务器检查与配置」→「安装 / 修复 / 重置预览」。这一步只读，展示用户、HOME、Shell、systemd 能力、监听端口、现有代理赋值与完整文件前后内容。
2. 查看差异。第三方配置保留；若端口冲突，修改 Portkey 端口或自行关闭旧隧道。Portkey 不判断未知配置无用，不清理 Clash/mihomo。
3. 勾选「我已查看以上变更」，再点击「应用预览中的变更并验证」。只有此操作才会写入服务器文件。
4. 每次提交先备份并记录事务，再校验预览摘要、逐文件原子替换，最后检查文件、Bash 登录环境和可用的 systemd 用户环境。安装后自动启动连接；以实际连接状态判断出口是否正常。文件成功不代表代理链路成功。
5. 在新登录 Shell 中验证程序访问。已运行的终端、用户服务和 VS Code Server 不会自动继承变量；按需由用户重启。

管理范围：

```text
~/.config/portkey/proxy.sh
~/.config/portkey/manifest.json
~/.config/environment.d/90-portkey.conf   # systemd 可用时
~/.bashrc                              # 标记包围的最小加载块
~/.bash_profile 或 ~/.bash_login 或 ~/.profile
```

只修改 Bash 实际优先读取的登录文件，并覆盖交互式非登录 Bash；加载块跳过普通非交互命令。不是向所有 profile 文件重复写入。HTTP-only / SOCKS5-only 会生成对应变量，Mixed 的 HTTP(S) 使用 HTTP，ALL_PROXY 使用 SOCKS5H。

修复 / 重置：共用同一个配置生成路径，将自有配置恢复到当前端口/协议的预期内容；正确配置不产生文件变更。缺失的自有文件可以恢复。已编辑的 Portkey 内容默认拒绝覆盖，只有显式勾选允许、重新预览并确认后才可替换。缺少/重复的块标记无法安全判定边界，需手工修复或恢复备份。

恢复：支持预览最近一次成功提交前的备份，当前文件若已改变则拒绝直接恢复。中断事务会阻止新部署；可勾选确认后「恢复中断的事务」，仅当内容仍等于事务写入值时回滚，否则保留用户编辑并要求人工检查。

卸载：先生成卸载预览并确认。只删除清单所拥有的文件和代码块；原来不存在且仍只含 Portkey 块的 Shell 文件也会移除。备份保留供恢复，不删除第三方文件、用户服务或其他 SSH 进程。systemd 清理仅针对仍匹配 Portkey 原值且无其他用户 environment.d 赋值的变量。

## 自动连接

每台服务器可以选择「启动时自动连接」。程序不再提供 Windows 登录自启动开关，也不创建注册表启动项或计划任务。

暂停只影响本次运行的自动重试；下一次启动是否连接由服务器的自动连接选项决定。退出会取消所有后台任务并结束 Portkey 创建的 SSH 进程。Windows Job Object 也用于进程异常退出时清理子进程。不会枚举或终止其他用户/应用创建的 SSH 会话。

网络恢复/休眠唤醒会唤醒检测；连接失败按 3、6、12…120 秒退避，本地代理未启动时等待，不反复创建 SSH 进程。

## 存储与安全

Windows 配置：`%LOCALAPPDATA%\Portkey\settings.json`，原子保存，上一版为 `settings.backup.json`。初始 schema 为 v1；未知版本拒绝读写，避免降级覆盖。配置只保存密钥路径，不存密码或私钥正文。

日志：`%LOCALAPPDATA%\Portkey\Logs\portkey.log`，2 MiB 后轮转，最多三个历史文件。异步有界队列，认证信息和密钥正文脱敏。诊断仍包含服务器显示名称等信息，分享前请自行审阅。远程文件内容不会写入日志。

远程备份与中断日志：`~/.local/state/portkey/`，最多保留 20 份备份，UI 提供最近一份恢复预览。目录/文件必须属于目标用户；拒绝配置路径中的符号链接、非 UTF-8/二进制或过大的文件。自定义 `XDG_CONFIG_HOME` 的自动部署暂不支持。

代理环境变量不是透明代理，只有遵守变量的程序才受影响。回环监听不向外网开放，但不隔离 Linux 同机其他用户的 TCP 访问。Windows 关机、休眠或本地代理停止时出口不可用。SSH 服务端需允许反向转发，以及用于健康探测的 direct-tcpip 通道；若只允许 remote forwarding，会报告无法验证，不能据此断言代理本身坏了。系统级 GatewayPorts 应由服务器管理员保持回环安全策略。健康请求验证完整网络链路，使用 Windows 的 TLS 信任库，不验证 Linux 程序自己的 CA 或现有进程环境。

## 构建与测试

仅需要 .NET 10 SDK；应用没有第三方 NuGet 业务依赖。跨平台核心与自动化测试是普通 net10.0 项目，桌面项目是 net10.0-windows。

Windows PowerShell：

```powershell
.\scripts\build.ps1
.\scripts\smoke-windows.ps1
```

`build.ps1 -Dotnet C:\path\to\dotnet.exe` 可指定 SDK。脚本先复制到本地唯一临时目录，避免 UNC 工作目录问题和 Linux/Windows 共享 obj 冲突，再测试、发布并同步 EXE。

WSL：

```bash
DOTNET=/path/to/dotnet ./scripts/build-wsl.sh
# 使用 Windows SDK 构建：
./scripts/build-wsl.sh --windows -Dotnet 'C:\path\to\dotnet.exe'
```

普通 Linux 构建也可通过 `EnableWindowsTargeting` 交叉发布，但不能据此声称 Windows UI 已实测。具体结果和人工验收矩阵见 [docs/TESTING.md](docs/TESTING.md)，架构/故障处理见 [docs/OPERATIONS.md](docs/OPERATIONS.md)。图标在 Windows 上运行 `scripts/make-icons.ps1` 重建，不依赖 Pillow。

## 完成范围与后续

UI 已按 Windows Flyout 规范重构：固定高度、固定返回与操作区、细滚动条、圆角裁剪、开合/切页动画，以及按任务栏 DPI 渲染的系统字形托盘图标。依据和原生组件验证见 [UI 设计记录](docs/UI-DESIGN.md)。

已实现：托盘面板/菜单、单实例、系统深浅主题和 DPI 感知、SSH Host 读取/解析、多服务器、首次认证入口、本地协议检测、真实反向转发、三层健康状态、退避/取消/恢复、原子配置与轮转日志，以及只读检查、部署预览、确认、所有权保护、幂等修复、备份恢复、保守卸载。

已在授权的 Ubuntu 20.04 测试机上通过 Windows OpenSSH 验证公钥认证、独立端口反向转发、HTTP/SOCKS5 实际返回 204、端口冲突、终止自有 SSH 后自动重连、暂停释放端口、失败不误报正常。随后在同一测试账户完成持久化部署，实际桌面程序已接管并保持 Healthy。详见 [连接测试记录](docs/LIVE-TESTING.md) 和 [持久化部署测试](docs/DEPLOYMENT-TESTING.md)。

尚未验证：真实服务器卸载/恢复、服务器重启后的环境、Windows 10、混合 DPI 多显示器、任务栏不同位置、Explorer 重启、实际断网/休眠，以及鼠标点击交互。此次 Windows UI 自动化因 WSL 路径初始化错误不可用；已做原生进程与 WPF 状态日志的启动验证。不能把这些未验证项当作验收通过。

尚未实现：Zsh/非 Debian 系发行版的自动配置、密码存储、任意历史 schema 迁移、代码签名/安装器、备份历史选择界面和自动识别迁移其他代理工具。CLI 沿用 OpenSSH 跳板/配置能力，但已有转发配置的 Host 需建立专用无转发别名，以免启动非 Portkey 管理的其他转发。

下一步优先完成真实设备验收和签名发布，再按实际需要扩展 Shell/发行版支持。项目不会未经预览确认自动部署服务器配置。
