# 验证记录与验收矩阵

日期：2026-10-21（Asia/Shanghai）。本文件区分实际文件/进程测试、模拟连接测试与尚待人工完成的真实设备验收。

## 自动化结果

| 验证 | 结果 | 范围 |
| --- | --- | --- |
| WSL .NET Release 测试 | 37 / 37 | 核心配置/部署规划、真实隔离 HOME 文件事务与 Bash 加载、实际 ssh -G、子进程超时清理、模拟连接状态机、探测协议/取消/权限错误 |
| Python Linux 事务测试 | 17 / 17 | 权限、字节保留、回滚、恢复、并发锁、所有权路径、systemd 调用边界 |
| Windows .NET Release 测试 | 37 / 37 | 共同核心测试、Windows OpenSSH -G、Job Object 下的超时子进程清理、HKCU Run 启用/禁用并恢复原值 |
| Windows 原生 WPF 外观/布局组件测试 | 55 / 55 | 深浅主题、固定高度、固定返回与操作区、细滚动条、圆角裁剪、动效、空状态、系统字形六种尺寸；26 张页面渲染图 |
| Windows x64 原生 SDK 发布 | 通过 | .NET SDK 10.0.402，Windows 11 10.0.26100，自包含单文件 WPF EXE |
| Windows 进程启动 smoke | 通过 | 进程存活、WPF 初始隐藏、第二实例正常退出、首个实例收到激活请求并报告面板可见/激活 |
| 真实服务器只读/临时隧道测试 | 11 / 11 | Windows OpenSSH 连接授权测试机，HTTP/SOCKS5 实际 204、冲突、断线重连、暂停、失败不误报 |
| 真实服务器持久化部署 | 通过 | 清理旧配置，关闭旧 17890 验证失败，正式组件部署五处文件；新 Bash/systemd 用户服务环境、Linux HTTPS 204、幂等、暂停保留配置、桌面接管均通过 |

Linux 还包含真实隔离 HOME 的远程辅助脚本/Bash 集成测试，屏蔽用户 DBus，避免接触真实 systemd 用户管理器。Windows 登录自启动功能及其注册表测试已移除。

`tests/Portkey.Tests` 是无需额外测试依赖的可执行测试程序；失败返回非零。`tests/linux/test_agent.py` 使用标准库 unittest，不依赖 pytest 或真实 SSH 服务器。常规构建中的远程辅助脚本事务测试在临时 HOME 中完成；显式授权的真实部署另见 DEPLOYMENT-TESTING.md。

Windows smoke 复制本次 EXE 到独立临时目录后启动；读取原生应用输出的 WPF 可见/激活状态日志。它不进行鼠标点击、不获取界面截图，也不证明外观/面板定位通过。最后只结束自身创建的应用进程；菜单「退出」仍列入人工验收。

最终 EXE 的大小和 SHA256 见 `artifacts/win-x64/build-info.txt`。构建没有第三方业务 NuGet 依赖。交叉编译也曾成功，但最终交付采用 Windows SDK 原生构建。

## Windows 验收矩阵

| 场景 | 已有证据 | 仍需真实设备验证 |
| --- | --- | --- |
| 首次无服务器 | 空配置单元测试，原生启动隐藏 | 空状态视觉、键盘操作 |
| 读取 SSH Host | Include/多别名测试，Windows ssh -G 实际执行 | 用户真实多层 Include/跳板组合 |
| 公钥认证成功 | Windows OpenSSH 实际 publickey 登录授权测试机 | 其他设备/agent 组合 |
| 认证失败 | 错误分类与退避模拟 | 实际密钥拒绝、口令 key、agent 中断 |
| 远程端口冲突 | 真实同端口第二隧道被拒绝，原监听保留 | 其他 sshd 策略组合 |
| 本地代理未启动 | 实际回环 socket 测试，等待状态模拟 | FlClash 退出/重新启动 |
| 隧道建立 | 真实 OpenSSH 转发确认与 Linux 回环监听 | 其他服务器策略 |
| SSH 活跃但代理失败 | 真实状态码不符保持 ProxyError；移除远程监听后探测不能绕过 | Linux 应用自身 CA/环境不在链路检测范围 |
| SSH 中断重连 | 终止自有实际 SSH 后自动重建并重新实际 HTTP/SOCKS5 通过 | SSH 服务暂时关闭后恢复 |
| 网络恢复 | Wake 提前结束退避模拟 | Wi-Fi 切换、断网恢复 |
| 主动暂停 | 真实隧道释放临时端口，Wake 不重连 | 托盘/面板点击和服务状态显示 |
| 用户退出 | 管理器 Dispose 与子进程终止测试 | 菜单退出、Windows 注销；手动 SSH 必须不受影响 |
| 第二实例 | Windows 实际启动第二进程，激活日志确认 | 鼠标触发的前台焦点体验 |
| 配置持久化 | 实际原子读写、备份、未来 schema 拒绝 | 磁盘满/杀进程恢复体验 |

原生布局与字形验证见 [UI-DESIGN.md](UI-DESIGN.md)。组件渲染覆盖深浅主题及 96/144 DPI；定位计算覆盖多种任务栏方向与缩放。

UI 真实设备专项仍需测试：Windows 10 降级、Windows 11 深/浅/高对比度、100/150/200% DPI、混合 DPI 多显示器、任务栏四边/自动隐藏/溢出托盘、Explorer 重启、左键切换、右键菜单、点击非激活窗口/任务栏后收起、Esc、Tab 与屏幕阅读器。

真实鼠标操作、多显示器 DPI 切换和系统合成的视觉效果仍需人工验收。本次没有伪造鼠标或截图测试结果。

## Linux 部署验收矩阵

| 场景 | 自动化覆盖 |
| --- | --- |
| 全新 HOME | 真实辅助脚本写入隔离 HOME，无预先清理 |
| 重复部署 | 无差异、无额外文件变更 |
| 端口修改 | 只改自有代理文件和摘要清单 |
| 修复、缺失文件 | 缺失自有文件重新生成；修改文件需显式授权 |
| Bash 已有用户内容 | 原始 CRLF 字节保留；无末尾换行保留；安装后真正运行交互/登录/普通非交互 Bash |
| 登录优先级 | .bash_profile 优先于 .bash_login/.profile 的规划测试 |
| systemd 可用 | 固定 user-only 命令与变量同步模拟；未声称真实 manager 验证通过 |
| systemd 不可用 | Shell-only 规划与辅助脚本处理 |
| 第三方配置 | 现有导出/其他 environment.d 文件警告且不删除 |
| 未知旧文件 | 未证明所有权时拒绝覆盖；路径白名单 |
| 备份/恢复 | 真正写入、更新、恢复；同秒多次提交仍选最新备份 |
| 中断部署 | 人工构造持久化 journal，验证恢复与保留并发编辑 |
| 权限不足 | 真实只读目录写失败，安全回滚 |
| 用户手动编辑 | 摘要差异、提交前并发修改拒绝 |
| 卸载自有内容 | 用户 Shell 内容保留，独占创建的空 Shell 文件移除 |
| 并发、符号链接、编码 | 真正的 flock 冲突、链接路径拒绝、非 UTF-8 拒绝 |

真实远程验收时应由用户指定普通测试账户，先只读检查，再由用户确认预览后写入。建议验证顺序：无配置 → 安装 → 新 Shell 环境与实际代理链路验证 → 重复安装 → 修改端口 → 恢复备份 → 手动追加无关内容 → 卸载。全程检查其他用户、其他 SSH 会话和原有第三方代理配置没有变化。

2026-10-21 已完成真实服务器只读与临时隧道测试，详见 [LIVE-TESTING.md](LIVE-TESTING.md)。服务器没有 curl，因此健康检测已改用 SSH stdio + Windows .NET，不依赖远程 HTTP 工具。随后经用户授权完成真实用户文件部署，新 Shell/systemd 用户服务与实际 Linux HTTPS 验证通过；桌面程序接管 17890。详见 [DEPLOYMENT-TESTING.md](DEPLOYMENT-TESTING.md)。真实服务器卸载/恢复及重启仍未验证。

## 复现命令

```bash
dotnet run --project tests/Portkey.Tests -c Release -m:1
python3 -m unittest discover -s tests/linux -v
```

```powershell
.\scripts\build.ps1 -Dotnet C:\path\to\dotnet.exe
.\scripts\smoke-windows.ps1
.\scripts\test-ui.ps1
```

Windows smoke 使用默认 `%LOCALAPPDATA%\Portkey`。应在尚无用户配置的环境执行，避免自动连接已有真实服务器；该脚本自身不会部署远程环境。
