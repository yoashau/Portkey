# 当前界面（2026-11-05）

Portkey 沿用 Intext 的暖色界面语言，保留 380×456 DIP 的紧凑托盘面板。正常高度的主页不滚动；工作区高度不足时主页可滚动，以保持连接和导航操作可达。连接设置与日志共用固定返回栏、固定底部操作区。下方旧记录保留作为历史，不代表当前尺寸或样式。

## 与 Intext 的对应关系

样式依据来自 Intext 当前源码的 `controls.css`、`settings.html` 与 `tray-menu.html`，在 WPF 中实现，不增加 WebView 或字体依赖。

| 元素 | 当前实现 |
| --- | --- |
| 界面字体 | Segoe UI Variable Text → Microsoft YaHei UI → Segoe UI，正文 15 DIP、字段标签半粗 |
| 数字与日志 | 端口沿用界面字体并启用等宽数字；日志保留 Cascadia Mono → Consolas → Microsoft YaHei UI |
| 浅色 / 深色底 | `#F7F4EF` 暖纸色 / `#1C1B19` 炭灰 |
| 主文字 | `#26231F` / `#F3EEE7` |
| 次级文字 | `#706B65` / `#BDB6AE` |
| 操作色 | 鼠尾草绿开关；浅色主按钮用更深的绿保证文字可读，深色主按钮用浅绿底配深色字 |
| 圆角与留白 | 面板 22 DIP（与 Intext 一致），端口区、浮层 12 DIP；输入框与按钮 9 DIP；左右通常 18 DIP |
| 输入与选择 | 34 DIP 紧凑控件；细边框，柔和的悬停与焦点背景；蓝色文字选区的透明度只计算一次 |
| 开关 | 38×22 DIP 轨道、16 DIP 滑块，保留原生 CheckBox 的键盘及辅助功能语义 |
| 帮助 | 字段旁 16 DIP 问号，悬停或键盘聚焦显示短提示，正文不堆放解释 |
| 托盘菜单 | 同套字体、暖色不透明浮层、常规字重与细分隔线 |

主页按服务器选择、连接状态、端口映射、连接操作、设置与日志入口排列。端口以单向箭头连接；不同的 SSH 地址显示在状态下，同名主机不重复显示。通知仍是一行固定位置的浮层，过长内容悬停查看，持久的同步警告不会扩大状态区。

设置按 SSH 身份、代理端口与协议、自动选项分组。开关文字与轨道均可点击；旁边的问号是独立帮助按钮，不触发开关，并向辅助功能暴露完整说明。键盘聚焦帮助时滚动到整行，避免开关或标签被底栏遮挡。「启动时自动连接」「新终端使用代理」使用右侧开关；协议显示为 `HTTP + SOCKS5`、`HTTP`、`SOCKS5`，保存的枚举值与行为不变。删除操作使用柔和的警示色。日志使用可选择、可复制的等宽文本区。

面板使用 WPF 的逐像素透明窗口，22 DIP 圆角的背景与内容裁剪分开绘制，避免在同一条边缘重复抗锯齿。参照 Intext 的透明边缘处理，关闭 DWM 原生边框、原生圆角与 Acrylic 背景，防止另一层材质从圆角后露出。背景使用不透明的暖纸色 / 炭灰色；高对比度使用系统画刷。整块面板滑入滑出只移动透明内容，不再用二值原生窗口区域裁剪圆角。滚动内容限制在视口内，避免盖住固定返回栏。系统动画关闭时不播放过渡。

文字保留原生 WPF 的字体选择、GlyphRun 排版、截断、换行、选择、输入与辅助功能，但通过 `OutlineTextRendering` 将实际 GlyphRun 转为几何轮廓绘制，绕开小尺寸字体栅格化对斜线的台阶化处理。浮动提示、下拉与托盘菜单使用同一绘制路径。绘制层位于本地 AdornerLayer 中，随面板一起运动，并重新应用滚动视口和祖先裁剪；共同祖先的透明度由视觉树自然继承，仅补偿文字独立分支的透明度。内容变化重建字形几何，位置或裁剪变化只重绘；可变裁剪/变换独立订阅，卸载时清理订阅并恢复原绘制。输入框复制原生 TextBoxView 的实际绘制树，包括选择标记与文本，因此不自行重算光标或选择范围。

单独切换 Display/Ideal、ClearType/Grayscale，或直接换用 WebView2 的原型都没有稳定消除标题斜线台阶，因此未向正式应用引入 WebView2、浏览器进程或新的字体依赖。字体、字重、字号和行内位置沿用当前设计，几何绘制以灰度覆盖过渡，不靠加粗掩盖边缘。

主页链条仍来自现有系统 Link 字形，以原始矢量轮廓绘制；不更改托盘和应用图标。主页与表单端口使用界面字体的 Tabular 数字，技术日志仍使用等宽字体。

本次只调整界面，SSH、连接、健康检测、配置同步与删除行为沿用现有实现。应用和托盘仍使用已有链条图标。

## 历史设计记录

# 托盘面板设计与验证

2026-10-21，Asia/Shanghai。

## 设计依据

- [Windows 圆角规范](https://learn.microsoft.com/en-us/windows/apps/design/style/rounded-corner)：弹出容器 8，内部控件 4。本实现使用逻辑像素（DIP），外层额外裁剪子内容，避免只有边框圆、内容仍为直角。按用户的实际视觉反馈，输入框、选择框、状态卡片、按钮各状态层及折叠区统一采用更明显的 8 DIP 圆角。
- [Windows Acrylic](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic) 与 [DWM 系统背景](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)：Windows 11 22621+ 使用 `DWMSBT_TRANSIENTWINDOW`；关闭透明、高对比度或不支持时使用纯色。
- [WinUI FlyoutPresenter 源码](https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/CommonStyles/FlyoutPresenter_themeresources.xaml) 与 [菜单源码](https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/CommonStyles/MenuFlyout_themeresources.xaml)：参考 1 像素边框、16 左右内距、原生行内圆角和独立菜单项布局（本应用统一加大为 8 DIP）。正文加大到 15，菜单行高 44，字段 14 半粗。
- [Windows 任务栏滑入／滑出规范](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion)与[动效时长](https://learn.microsoft.com/en-us/windows/apps/design/motion/timing-and-easing)：切页仅平移 100ms，不从透明开始；整块面板从底部滑入 250ms，向下滑出 167ms；进入减速、退出加速。遵守系统动画和高对比度设置。
- 本地 `Aulay/TrayPanel.hpp`：以图标水平中心和图标顶部减 12 DIP 为锚点；屏幕边缘才避让。顶部和侧边任务栏另做边界计算。

## 最终布局

面板固定为 380×540 DIP，切页与展开选项不改变外容器高度；屏幕空间不足时缩小到工作区允许的尺寸。主页使用无外框的连接状态区和端口映射，顶部仅保留服务器切换。SSH 地址与显示名称相同时不重复展示；不同时保留地址。未添加服务器时使用留白、链条图标、简短标题和明确的「添加服务器」主按钮，不再使用大块卡片；该按钮直接打开新连接表单，服务器配置入口禁用。连接和重连按钮统一为 44 DIP 高。移除品牌标题栏、关闭按钮、状态圆点及说明小字。

次级页面的返回栏固定在顶部；保存、部署确认和主要操作固定在底部。日志填满可用区域，默认部署页仅预览框自身滚动。连接设置、服务器配置和日志页始终保留外层滚动条的位置，展开「备份与修复」时内容宽度不变；首页不预留该位置。备份和事务恢复归入「备份与修复」。

滚动条轨道透明，无箭头；可点击区域保留 12 DIP，静止线条为 2 DIP、弱透明，悬停/拖动最多 4 DIP。滚动条位于面板最右侧。外容器与大型预览/日志框使用 8 DIP 圆角，输入框与选择框 8 DIP，外层显式裁剪子内容。

输入框与复选框继承 WPF Fluent 模板。ComboBox 保留原生选择、键盘及辅助功能逻辑与 PART_Popup；外观与 Expander 共用 DisclosureToggle 模板，统一圆角、边框、字重、箭头及悬停状态。移除下拉浮层的 90 DIP 平移和软件阴影，仅使用 100ms 短淡入；折叠区用 140ms 高度过渡，并同步滚动到标题附近，收起后隐藏内容以免键盘访问。用户滚动、点击与切页会终止自动滚动。输入框通过 Border.CornerRadius 及局部 ControlCornerRadius 资源保持内容边框和悬停层一致。托盘右键菜单复用原生 ContextMenu 外层模板；其中的平铺操作使用 8 DIP 圆角、40 DIP 行高、15 DIP 半粗文字及相同的悬停配色，背景为与面板纯色回退一致的不透明色，避免独立弹窗透出桌面文字。该菜单样式仅作用于托盘菜单，文本框的原生编辑菜单保留。右键菜单以托盘图标向右上方展开，屏幕边缘避让；复用主面板的物理坐标定位和 12 DIP 间距计算，避免随鼠标点击高度改变与任务栏的距离。

2026-10-21 修复样式加载回归：正式 App.xaml 以独立 ResourceDictionary 作为根、将 FlyoutStyles.xaml 合并进去；不再将它直接设为根字典 Source。原结构在正式 App.InitializeComponent 路径下会落回直角旧模板，而手动创建 Application 的测试没有复现。回归测试现在加载正式编译的 App.xaml，并在改变任何主题前先验证初始 System 模式；仅取消测试进程中排队的真实启动回调，防止访问用户配置与 SSH。

显示窗口前先完成目标显示器的 DPI、尺寸和托盘定位。面板整体滑动期间，使用原生窗口区域同步裁剪 DWM 背景，防止先显示空白背景再出现内容；动画完成后移除临时区域，恢复系统阴影和圆角。重复打开从当前位置继续，取消过期收起回调。

## 托盘图标

[微软系统图标规范](https://learn.microsoft.com/windows/apps/design/signature-experiences/iconography)明确 Segoe Fluent Icons 为单线风格、基础笔画 1 epx，字形对应方形 em 尺寸。

应用与托盘共用系统字体的 Link 字形，旋转为 Portkey 的斜向链条轮廓，保持系统笔画。根据实际任务栏 HWND 的 DPI，调用 `GetSystemMetricsForDpi(SM_CXSMICON)` 得到物理尺寸并直接渲染透明 HICON；不叠加状态标记。Windows 10 使用系统字体回退。应用 ICO 和 PNG 由 `scripts/make-icons.ps1` 调用同一份 `TrayIcon.cs` 生成，原始用户素材保留。ICO 各尺寸与托盘绘制结果逐像素比较，避免再次分成两套轮廓。

在 100% 下，链条主体为 14×14，系统音量参考为 16×14，Wi-Fi 为 16×11；200% 下分别为 28×28、32×26、32×23。统一原生字形尺度与笔画，不强行把不同形状拉伸成同一个矩形。

## 已验证范围

- Windows 原生 WPF 组件测试通过，覆盖固定高度、长表单固定返回/保存、最右侧细滚动条、预览圆角、默认页面无多层滚动、输入框聚焦与选择框展开的圆角、备份工具展开前后宽度一致、按钮等高、原生背景和内容同步滑动、首个可见帧已完成托盘定位、动画结束收起、快速重开取消收起、空状态，以及 16/20/24/32/40/48 像素系统字形。
- 系统、浅色和深色主题各页及托盘右键菜单以 96/144 DPI 导出组件渲染图，另有 18 张图标尺寸参考。组件渲染使用纯色回退，不包含 DWM 桌面背景，也不代表鼠标点击验收。
- 定位纯函数测试覆盖图标居中、12 DIP 偏移、四边任务栏、屏幕避让、负坐标显示器和 150% 缩放。
- 实际桌面程序在 200% 缩放启动；定位及图标尺寸记录在 `%LOCALAPPDATA%\Portkey\Logs\portkey.log`。实际 SSH/HTTP/SOCKS5 状态另行验证，不由布局测试伪造。

运行 `.\scripts\test-ui.ps1` 可复现，输出在 `artifacts/ui/`。测试使用临时模型，不访问 SSH、不保存用户设置、不进行桌面输入自动化。

真实鼠标操作、多显示器 DPI 切换、Windows 10、高对比度实际切换和系统合成的视觉效果仍需人工验收。

## 响应与自启动移除（2026-10-21）

切页不再从全透明开始，只使用 100ms 小幅平移。SSH Host 列表改在后台读取，不阻塞打开连接设置；状态更新只修改托盘提示，不再重绘图标、重建整套主题资源。主题缓存按明暗、透明、高对比度和 HWND 失效；真实主题变更强制刷新。

删除 Windows 登录自启动页面、右键设置入口、WindowsStartup.cs 与注册表写入测试；每台服务器「启动时自动连接」仍保留。本次本地更新已检查当前用户的 Run 项，没有 Portkey 残留；应用不保留迁移清理代码。

[微软社区](https://techcommunity.microsoft.com/discussions/windows11/small-freeze-when-i-open-the-quick-settings-menu/3030374)存在快速设置卡顿反馈，不能据此推断本机或本应用的剩余延迟属于系统。[WPF .NET 10 原生 Expander 模板](https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/Themes/PresentationFramework.Fluent/Styles/Expander.xaml)包含 333ms 展开动画与 200ms 延迟隐藏；本实现替换了这一交互。组件测试中的 handler 耗时只表示事件处理执行时间，不等于真实鼠标到屏幕显示延迟或稳定帧率。
