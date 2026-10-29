using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Portkey.Core;
using Portkey.Infrastructure;

namespace Portkey.App;

public partial class TrayPanel : Window
{
    private readonly AppViewModel model;
    private CancellationTokenSource? operation;
    private Guid editId;
    private RemoteSnapshot? snapshot;
    private DeploymentPlan? plan;
    private Guid? planServer;
    private bool deleteArmed;
    private DateTimeOffset deleteArmedAt;
    private int presentationVersion;
    private (bool Dark, bool Transparency, bool Contrast, IntPtr Window)? appliedTheme;
    private static readonly DependencyProperty PageOffsetProperty = DependencyProperty.Register("PageOffset", typeof(double), typeof(TrayPanel), new PropertyMetadata(0.0, (d, e) => ((TrayPanel)d).PageScroll.ScrollToVerticalOffset((double)e.NewValue)));
    private readonly TranslateTransform surfaceTranslation = new();
    public bool IsDismissing { get; private set; }
    public TrayPanel(AppViewModel model)
    {
        this.model = model;
        InitializeComponent();
        Surface.RenderTransform = surfaceTranslation;
        surfaceTranslation.Changed += (_, _) => UpdateSlideClip();
        Surface.SizeChanged += (_, _) => Surface.Clip = new RectangleGeometry(new Rect(Surface.RenderSize), 8, 8);
        DataContext = model;
        ProtocolInput.ItemsSource = Enum.GetValues<ProxyProtocol>();
        ProtocolInput.SelectedIndex = 0;
        PageScroll.PreviewMouseWheel += (_, _) => StopPageScroll();
        PageScroll.PreviewMouseDown += (_, _) => StopPageScroll();
        model.SelectionChanged += ClearPlan;
        model.StatusChanged += UpdateEmptyState;
        UpdateEmptyState();
        SourceInitialized += (_, _) => ApplyTheme();
        Closing += (_, e) => { e.Cancel = true; Dismiss(); };
    }

    public void ApplyTheme(bool force = false)
    {
        using var registry = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
#pragma warning disable WPF0001 // Match the existing Fluent ThemeMode in App.xaml, including explicit theme previews.
        bool dark = Application.Current.ThemeMode.Equals(ThemeMode.Dark) ||
            (Application.Current.ThemeMode.Equals(ThemeMode.System) && registry?.GetValue("AppsUseLightTheme") is int value && value == 0);
#pragma warning restore WPF0001
        bool transparency = registry?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
        var handle = new WindowInteropHelper(this).Handle;
        var theme = (dark, transparency, SystemParameters.HighContrast, handle);
        if (!force && appliedTheme == theme) return;
        appliedTheme = theme;
        bool acrylic = NativeWindowStyle.Apply(handle, dark, transparency && !SystemParameters.HighContrast);
        void Brush(string name, string color) => Application.Current.Resources[name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        Brush("PaneBrush", acrylic ? (dark ? "#14000000" : "#10FFFFFF") : (dark ? "#282828" : "#F9F9F9"));
        Brush("MenuBrush", dark ? "#282828" : "#F9F9F9");
        Brush("TextBrush", dark ? "#F5F5F5" : "#202020");
        Brush("SecondaryTextBrush", dark ? "#D0D0D0" : "#606060");
        Brush("BorderBrush", dark ? "#28FFFFFF" : "#26000000");
        Brush("HoverBrush", dark ? "#0FFFFFFF" : "#09000000");
        Brush("PressedBrush", dark ? "#18FFFFFF" : "#14000000");
        Brush("ControlBrush", dark ? "#0FFFFFFF" : "#70FFFFFF");
        Brush("ThumbBrush", dark ? "#B0FFFFFF" : "#80000000");
        Brush("AccentBrush", dark ? "#99CFFF" : "#0067C0");
        Brush("AccentTextBrush", dark ? "#15212B" : "#FFFFFF");
        if (SystemParameters.HighContrast)
        {
            var resources = Application.Current.Resources;
            resources["PaneBrush"] = resources["MenuBrush"] = SystemColors.WindowBrush;
            resources["TextBrush"] = resources["SecondaryTextBrush"] = SystemColors.WindowTextBrush;
            resources["BorderBrush"] = resources["ThumbBrush"] = SystemColors.WindowTextBrush;
            resources["HoverBrush"] = resources["PressedBrush"] = resources["ControlBrush"] = SystemColors.ControlBrush;
            resources["AccentBrush"] = SystemColors.HighlightBrush;
            resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
        }
    }

    private void UpdateEmptyState()
    {
        bool empty = model.Servers.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DeploymentNav.IsEnabled = !empty;
        ServerSelector.Visibility = ConnectionCard.Visibility = ConnectionActions.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Navigate(FrameworkElement page)
    {
        deleteArmed = false;
        StopPageScroll();
        foreach (var item in new FrameworkElement[] { HomePage, EditorPage, DeployPage, LogsPage }) item.Visibility = item == page ? Visibility.Visible : Visibility.Collapsed;
        NavigationHeader.Visibility = page == HomePage ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = page == EditorPage ? "连接设置" : page == DeployPage ? "服务器配置" : "连接日志";
        SaveButton.Visibility = page == EditorPage ? Visibility.Visible : Visibility.Collapsed;
        DeployActions.Visibility = page == DeployPage ? Visibility.Visible : Visibility.Collapsed;
        LogActions.Visibility = page == LogsPage ? Visibility.Visible : Visibility.Collapsed;
        ActionFooter.Visibility = page == HomePage ? Visibility.Collapsed : Visibility.Visible;
        PageScroll.VerticalScrollBarVisibility = page == HomePage ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Visible;
        PageScroll.ScrollToTop();
        model.Message = "";
        if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
        {
            // Keep clicked content visible immediately; a short translation supplies continuity.
            PageTranslation.BeginAnimation(TranslateTransform.XProperty, Animation(page == HomePage ? -6 : 6, 0, 100));
        }
    }
    private void StopPageScroll()
    {
        double offset = PageScroll.VerticalOffset;
        BeginAnimation(PageOffsetProperty, null);
        SetValue(PageOffsetProperty, offset);
    }
    private void DisclosureChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander || !expander.IsVisible || e.OriginalSource != expander) return;
        expander.ApplyTemplate();
        var reveal = (Grid)expander.Template.FindName("Reveal", expander);
        var body = (Border)expander.Template.FindName("Body", expander);
        bool expanded = expander.IsExpanded;
        double from = reveal.ActualHeight;
        body.Visibility = Visibility.Visible;
        body.Measure(new Size(Math.Max(0, expander.ActualWidth), double.PositiveInfinity));
        double height = expanded ? body.DesiredSize.Height : 0;
        double absoluteHeader = expander.TranslatePoint(new Point(), PageScroll).Y + PageScroll.VerticalOffset;
        double maxOffset = Math.Max(0, PageScroll.ScrollableHeight + height - from);
        double target = Math.Min(maxOffset, expanded ? Math.Max(PageScroll.VerticalOffset, absoluteHeader - 12) : PageScroll.VerticalOffset);
        StopPageScroll();
        reveal.BeginAnimation(HeightProperty, null);
        bool animate = SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        if (!animate)
        {
            reveal.Height = expanded ? double.NaN : 0;
            body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            PageScroll.ScrollToVerticalOffset(target);
            return;
        }
        reveal.Height = from;
        var slide = Animation(from, height, 140);
        slide.Completed += (_, _) =>
        {
            if (expander.IsExpanded != expanded) return;
            reveal.BeginAnimation(HeightProperty, null);
            reveal.Height = expanded ? double.NaN : 0;
            body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        };
        reveal.BeginAnimation(HeightProperty, slide);
        SetValue(PageOffsetProperty, PageScroll.VerticalOffset);
        var scroll = Animation(PageScroll.VerticalOffset, target, 140);
        scroll.Completed += (_, _) => StopPageScroll();
        BeginAnimation(PageOffsetProperty, scroll);
    }
    private static DoubleAnimationUsingKeyFrames Animation(double from, double to, int milliseconds, bool exit = false)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)), new KeySpline(exit ? 1 : 0, 0, exit ? 1 : 0, 1)));
        return animation;
    }
    private double SlideHeight => Math.Min(Height, MaxHeight);
    private void UpdateSlideClip()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            NativeWindowStyle.SetSlideClip(handle, surfaceTranslation.Y, Width, SlideHeight, VisualTreeHelper.GetDpi(this).DpiScaleY);
    }
    public void AnimateOpen()
    {
        double from = IsVisible ? surfaceTranslation.Y : SlideHeight;
        presentationVersion++;
        IsDismissing = false;
        surfaceTranslation.BeginAnimation(TranslateTransform.YProperty, null);
        bool animate = SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        // Establish the hidden first frame before the animation clock ticks.
        surfaceTranslation.Y = animate ? from : 0;
        UpdateSlideClip();
        if (!animate) return;
        // Clip the native backdrop along with the content, so no empty acrylic box appears first.
        surfaceTranslation.BeginAnimation(TranslateTransform.YProperty, Animation(from, 0, 250));
        UpdateSlideClip();
    }
    public void Dismiss()
    {
        if (!IsVisible || IsDismissing) return;
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) { Hide(); return; }
        IsDismissing = true;
        int version = ++presentationVersion;
        var slide = Animation(surfaceTranslation.Y, SlideHeight, 167, exit: true);
        slide.Completed += (_, _) => { if (version == presentationVersion) { Hide(); IsDismissing = false; } };
        surfaceTranslation.BeginAnimation(TranslateTransform.YProperty, slide);
    }
    public void ShowLogs() { Navigate(LogsPage); LogsText.Text = model.Diagnostics(); }
    public void CancelWork() => operation?.Cancel();
    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (model.Busy) return;
        deleteArmed = false;
        model.Busy = true;
        model.Message = "正在处理…";
        operation = new CancellationTokenSource();
        CancelButton.Visibility = Visibility.Visible;
        try { await work(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { model.Message = "操作已取消。若远程写入被中断，请先重新检查，必要时恢复中断事务。"; }
        catch (OperationCanceledException) { model.Message = "操作超时。请重试。"; }
        catch (Exception error) { model.Message = error.Message; model.Logger.Write("ui", error.Message); }
        finally { operation.Dispose(); operation = null; model.Busy = false; CancelButton.Visibility = Visibility.Collapsed; UpdateEmptyState(); }
    }
    private ServerProfile RequireServer() => model.Selected?.Profile ?? throw new InvalidOperationException("请先添加并保存一个 SSH 服务器。");
    private void ClearPlan()
    {
        deleteArmed = false;
        plan = null; snapshot = null; planServer = null;
        if (ApplyButton is not null) { ApplyButton.IsEnabled = false; ConfirmInput.IsChecked = false; PreviewText.Text = ""; }
    }
    private void InvalidatePlan(object sender, RoutedEventArgs e) => ClearPlan();
    private void GoHome(object sender, RoutedEventArgs e) { Navigate(HomePage); UpdateEmptyState(); }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) { GoHome(sender, e); e.Handled = true; }
    }
    private void CancelOperation(object sender, RoutedEventArgs e) => CancelWork();
    private async void ToggleConnection(object sender, RoutedEventArgs e) => await RunAsync(async _ => { await model.ToggleAsync(); model.Message = ""; });
    private async void Reconnect(object sender, RoutedEventArgs e) => await RunAsync(async _ => { if (model.Selected is not null) await model.Selected.Manager.ReconnectAsync(); model.Message = ""; });
    private void OpenDeployment(object sender, RoutedEventArgs e) { ClearPlan(); Navigate(DeployPage); }
    private void OpenLogs(object sender, RoutedEventArgs e) => ShowLogs();
    private async void OpenEditor(object sender, RoutedEventArgs e)
    {
        Navigate(EditorPage);
        LoadEditor(model.Selected?.Profile ?? new ServerProfile());
        try
        {
            var aliases = await Task.Run(() => model.ConfigReader.ReadAliases(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config")));
            await Dispatcher.InvokeAsync(() => { if (EditorPage.IsVisible) HostAliases.ItemsSource = aliases; });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            model.Logger.Write("ssh-config", error.Message);
        }
        catch (TaskCanceledException) when (Dispatcher.HasShutdownStarted) { }
    }
    private void NewServer(object sender, RoutedEventArgs e) => LoadEditor(new ServerProfile());
    private void LoadEditor(ServerProfile server)
    {
        editId = server.Id;
        NameInput.Text = server.Name; HostInput.Text = server.Host; UserInput.Text = server.User;
        SshPortInput.Text = server.SshPort?.ToString() ?? ""; IdentityInput.Text = server.IdentityFile;
        LocalPortInput.Text = server.LocalPort.ToString(); RemotePortInput.Text = server.RemotePort.ToString();
        ProtocolInput.SelectedItem = server.Protocol; AutoInput.IsChecked = server.AutoConnect;
        CheckUrlInput.Text = server.CheckUrl; StatusInput.Text = server.ExpectedStatus.ToString(); TimeoutInput.Text = server.TimeoutSeconds.ToString();
    }
    private ServerProfile ReadEditor()
    {
        if (ProtocolInput.SelectedItem is not ProxyProtocol protocol) throw new ArgumentException("请选择代理协议。");
        var profile = new ServerProfile
        {
            Id = editId, Name = NameInput.Text.Trim(), Host = HostInput.Text.Trim(), User = UserInput.Text.Trim(),
            SshPort = ServerProfile.ParseOptionalPort(SshPortInput.Text), IdentityFile = IdentityInput.Text.Trim(),
            LocalPort = ServerProfile.ParseRequiredInt(LocalPortInput.Text, "本地代理端口"),
            RemotePort = ServerProfile.ParseRequiredInt(RemotePortInput.Text, "远程监听端口"), Protocol = protocol,
            AutoConnect = AutoInput.IsChecked == true, CheckUrl = CheckUrlInput.Text.Trim(),
            ExpectedStatus = ServerProfile.ParseRequiredInt(StatusInput.Text, "预期状态码"),
            TimeoutSeconds = ServerProfile.ParseRequiredInt(TimeoutInput.Text, "检测超时")
        };
        profile.Validate();
        return profile;
    }
    private void ImportAlias(object sender, SelectionChangedEventArgs e)
    {
        if (HostAliases.SelectedItem is string alias) { HostInput.Text = alias; NameInput.Text = alias; UserInput.Text = ""; SshPortInput.Text = ""; IdentityInput.Text = ""; }
    }
    private async void SaveServer(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var profile = ReadEditor();
        await model.SaveAsync(profile, ct);
        ClearPlan(); Navigate(HomePage); model.Message = "连接已保存。";
    });
    private async void DeleteServer(object sender, RoutedEventArgs e)
    {
        if (!deleteArmed || DateTimeOffset.Now - deleteArmedAt < TimeSpan.FromMilliseconds(500))
        {
            deleteArmed = true;
            deleteArmedAt = DateTimeOffset.Now;
            model.Message = "再次点击「移除本地连接」确认。只删除本机配置，远程文件保留。";
            return;
        }
        deleteArmed = false;
        await RunAsync(async _ => { await model.DeleteAsync(); ClearPlan(); Navigate(HomePage); model.Message = "已移除本地配置。远程文件保留。"; });
    }
    private async void InitializeSsh(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var server = ReadEditor(); server.Validate();
        var start = SshCommand.Create(server, interactive: true);
        start.ArgumentList.Add("-o"); start.ArgumentList.Add("ClearAllForwardings=yes");
        SshCommand.AddDestination(start, server.Host);
        start.ArgumentList.Add("printf '\\nPortkey SSH authentication ready\\n'; sleep 2");
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Windows OpenSSH。");
        using var cancel = ct.Register(() => SshCommand.Kill(process));
        await process.WaitForExitAsync(ct);
        model.Message = process.ExitCode == 0 ? "SSH 交互认证成功。请保存配置，再启动后台连接。" : "SSH 认证未完成；请检查主机指纹、密钥和 ssh-agent。";
    });
    private async void CheckLocalProxy(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        model.Message = await ProxyHealthChecker.CheckLocalAsync(ReadEditor(), ct);
    });
    private async Task PreviewAsync(bool uninstall, CancellationToken ct)
    {
        ClearPlan();
        var server = RequireServer();
        snapshot = await model.Inspector.InspectAsync(server, ct);
        plan = LinuxDeploymentPlanner.Create(snapshot, server, uninstall, AllowEditedInput.IsChecked == true);
        planServer = server.Id;
        PreviewText.Text = plan.Preview(snapshot) + "\n\n远程监听端口（只读）：\n" + snapshot.Listeners + "\n\n当前 systemd 代理环境：\n" + snapshot.SystemdEnvironment + "\n\n当前 SSH 会话代理环境：\n" + snapshot.ShellEnvironment;
        ApplyButton.IsEnabled = true;
        model.Message = "只读检查完成。应用前请查看差异并勾选确认。";
    }
    private async void InspectInstall(object sender, RoutedEventArgs e) => await RunAsync(ct => PreviewAsync(false, ct));
    private async void InspectUninstall(object sender, RoutedEventArgs e) => await RunAsync(ct => PreviewAsync(true, ct));
    private async void PreviewRestore(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        ClearPlan();
        var server = RequireServer();
        snapshot = await model.Inspector.InspectAsync(server, ct);
        var backup = snapshot.Backups.FirstOrDefault() ?? throw new InvalidOperationException("没有可恢复的已提交备份。");
        foreach (var change in backup.Restore)
            if (snapshot.Files[change.Path].Hash != change.BeforeHash) throw new InvalidOperationException("备份后文件已改变；为保护用户编辑，无法直接恢复。请手动审阅远程备份。");
        plan = new DeploymentPlan("恢复备份 " + backup.Id, backup.Restore, ["恢复最近一次提交前的文件；此操作也会备份当前文件。"], snapshot.Systemd);
        planServer = server.Id;
        PreviewText.Text = plan.Preview(snapshot);
        ApplyButton.IsEnabled = true;
        model.Message = "恢复预览已生成，请勾选确认。";
    });
    private async void ApplyPlan(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        if (ConfirmInput.IsChecked != true || plan is null || snapshot is null || planServer != model.Selected?.Profile.Id) throw new InvalidOperationException("请先查看当前服务器的预览并勾选确认。");
        var server = RequireServer();
        bool connect = plan.Operation.StartsWith("安装", StringComparison.Ordinal);
        string report = await model.Executor.ApplyAsync(server, snapshot, plan, ct);
        PreviewText.Text = report;
        plan = null; ApplyButton.IsEnabled = false; ConfirmInput.IsChecked = false;
        model.Message = "文件应用结束，验证结果见上方。可通过主页连接，检测真实代理链路。";
        if (connect && model.Selected is not null)
        {
            await model.Selected.Manager.StartAsync();
            model.Message = "文件验证结果见上方。正在建立隧道并检测实际出口，连接状态会持续更新。";
        }
    });
    private async void RecoverTransaction(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        if (ConfirmInput.IsChecked != true) throw new InvalidOperationException("恢复会尝试回滚中断事务中属于 Portkey 的文件修改，请先勾选确认。");
        var server = RequireServer();
        string result = await model.Inspector.RecoverAsync(server, ct);
        ClearPlan(); PreviewText.Text = result; model.Message = "恢复操作结束，请重新生成检查预览。";
    });
    private void RefreshLogs(object sender, RoutedEventArgs e) => LogsText.Text = model.Diagnostics();
    private void CopyDiagnostics(object sender, RoutedEventArgs e) { Clipboard.SetText(model.Diagnostics()); model.Message = "诊断信息已复制，请在分享前审阅服务器名称。"; }
    private void OpenLogFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(model.Logger.DirectoryPath);
        Process.Start(new ProcessStartInfo(model.Logger.DirectoryPath) { UseShellExecute = true });
    }
}
