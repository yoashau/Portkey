using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
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
    private bool suppressImport;
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
        Brush("NoticeWarningBrush", "#D9A441");
        if (SystemParameters.HighContrast)
        {
            var resources = Application.Current.Resources;
            resources["PaneBrush"] = resources["MenuBrush"] = SystemColors.WindowBrush;
            resources["TextBrush"] = resources["SecondaryTextBrush"] = SystemColors.WindowTextBrush;
            resources["BorderBrush"] = resources["ThumbBrush"] = SystemColors.WindowTextBrush;
            resources["HoverBrush"] = resources["PressedBrush"] = resources["ControlBrush"] = SystemColors.ControlBrush;
            resources["AccentBrush"] = SystemColors.HighlightBrush;
            resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
            resources["NoticeWarningBrush"] = SystemColors.WindowTextBrush;
        }
    }

    private void UpdateEmptyState()
    {
        bool empty = model.Servers.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ServerSelector.Visibility = ConnectionCard.Visibility = ConnectionActions.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Navigate(FrameworkElement page)
    {
        deleteArmed = false;
        StopPageScroll();
        deleteArmed = false;
        foreach (var item in new FrameworkElement[] { HomePage, EditorPage, LogsPage }) item.Visibility = item == page ? Visibility.Visible : Visibility.Collapsed;
        NavigationHeader.Visibility = page == HomePage ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = page == EditorPage ? "连接设置" : "连接日志";
        EditorActions.Visibility = page == EditorPage ? Visibility.Visible : Visibility.Collapsed;
        UpdateFollowUp();
        LogActions.Visibility = page == LogsPage ? Visibility.Visible : Visibility.Collapsed;
        ActionFooter.Visibility = page == HomePage ? Visibility.Collapsed : Visibility.Visible;
        PageScroll.VerticalScrollBarVisibility = page == HomePage ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Visible;
        PageScroll.ScrollToTop();
        model.ClearMessage();
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
        model.ShowMessage("正在处理", transient: false);
        operation = new CancellationTokenSource();
        try { await work(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { model.ShowMessage("操作已取消。若远程写入中断，请重新检查，并在需要时恢复中断的写入。"); }
        catch (OperationCanceledException) { model.ShowMessage("操作超时。请重试。"); }
        catch (Exception error) { model.ShowMessage(error.Message); model.Logger.Write("ui", error.Message); }
        finally { operation.Dispose(); operation = null; model.Busy = false; UpdateEmptyState(); }
    }
    private ServerProfile RequireServer() => model.Selected?.Profile ?? throw new InvalidOperationException("请先添加并保存一个 SSH 服务器。");
    private void SyncChanged(object sender, RoutedEventArgs e) { if (SaveButton is not null) SaveButton.Content = SyncInput.IsChecked == true ? "保存并同步" : "保存"; }
    private void ClearPlan() { deleteArmed = false; }
    private void GoHome(object sender, RoutedEventArgs e) { Navigate(HomePage); UpdateEmptyState(); }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) { GoHome(sender, e); e.Handled = true; }
    }
    private async void ToggleConnection(object sender, RoutedEventArgs e) => await RunAsync(async _ => { await model.ToggleAsync(); model.ClearMessage(); });
    private bool reconnecting;
    private async void Reconnect(object sender, RoutedEventArgs e)
    {
        // Connection status already reports progress. A page-wide busy state would fade every control and open the message bar.
        if (reconnecting || model.Busy || model.Selected is null) return;
        reconnecting = true;
        try { await model.Selected.Manager.ReconnectAsync(); }
        catch (Exception error) { model.ShowMessage(error.Message); model.Logger.Write("ui", error.Message); }
        finally { reconnecting = false; }
    }
    private void OpenLogs(object sender, RoutedEventArgs e) => ShowLogs();
    private async void OpenEditor(object sender, RoutedEventArgs e)
    {
        Navigate(EditorPage);
        LoadEditor(model.Selected?.Profile ?? new ServerProfile());
        try
        {
            var aliases = await Task.Run(() => model.ConfigReader.ReadAliases(SshConfigPath()));
            await Dispatcher.InvokeAsync(() =>
            {
                if (!EditorPage.IsVisible) return;
                suppressImport = true;
                try { HostAliases.ItemsSource = aliases; HostAliases.SelectedIndex = -1; }
                finally { suppressImport = false; }
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            model.Logger.Write("ssh-config", error.Message);
        }
        catch (TaskCanceledException) when (Dispatcher.HasShutdownStarted) { }
    }
    private void NewServer(object sender, RoutedEventArgs e)
    {
        suppressImport = true;
        try { HostAliases.SelectedIndex = -1; }
        finally { suppressImport = false; }
        LoadEditor(new ServerProfile());
    }
    private void LoadEditor(ServerProfile server)
    {
        editId = server.Id;
        NameInput.Text = server.Name; HostInput.Text = server.Host; UserInput.Text = server.User;
        SshPortInput.Text = server.SshPort?.ToString() ?? ""; IdentityInput.Text = server.IdentityFile;
        LocalPortInput.Text = server.LocalPort.ToString(); RemotePortInput.Text = server.RemotePort.ToString();
        ProtocolInput.SelectedItem = server.Protocol; AutoInput.IsChecked = server.AutoConnect; SyncInput.IsChecked = server.SyncRemoteEnvironment;
        SyncChanged(SyncInput, new RoutedEventArgs());
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
            AutoConnect = AutoInput.IsChecked == true, SyncRemoteEnvironment = SyncInput.IsChecked == true,
            CheckUrl = ServerProfile.FixedCheckUrl, ExpectedStatus = ServerProfile.FixedExpectedStatus, TimeoutSeconds = ServerProfile.FixedTimeoutSeconds
        };
        profile.Validate();
        return profile;
    }
    private static string SshConfigPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
    private async void ImportAlias(object sender, SelectionChangedEventArgs e)
    {
        if (suppressImport || HostAliases.SelectedItem is not string alias) return;
        HostInput.Text = alias;
        NameInput.Text = alias;
        SpecifiedSshSettings specified;
        bool exists = File.Exists(SshConfigPath());
        try { specified = await Task.Run(() => model.ConfigReader.ReadSpecified(SshConfigPath(), alias)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            model.Logger.Write("ssh-config", error.Message);
            if (HostAliases.SelectedItem as string != alias || !EditorPage.IsVisible) return;
            UserInput.Text = ""; SshPortInput.Text = ""; IdentityInput.Text = "";
            model.ShowMessage("未能读取 SSH 配置，用户名、端口和私钥保持留空。");
            return;
        }
        if (HostAliases.SelectedItem as string != alias || !EditorPage.IsVisible) return;
        UserInput.Text = specified.User ?? "";
        SshPortInput.Text = specified.Port?.ToString() ?? "";
        IdentityInput.Text = specified.IdentityFile ?? "";
        if (!exists) model.ShowMessage("未找到 SSH 配置文件，用户名、端口和私钥保持留空。"); else model.ClearMessage();
    }
    private async void SaveServer(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var profile = ReadEditor();
        string message = await model.SaveAsync(profile, replaceEdited: false, ct);
        if (message.StartsWith("没有同步", StringComparison.Ordinal))
        {
            UpdateFollowUp();
            model.ShowMessage(message);
            return;
        }
        Navigate(HomePage); model.ShowMessage(message);
    });
    private void UpdateFollowUp()
    {
        if (FollowUpButton is null) return;
        FollowUpButton.Visibility = model.SyncFollowUp is "replace" or "recover" ? Visibility.Visible : Visibility.Collapsed;
        FollowUpButton.Content = model.SyncFollowUp == "recover" ? "恢复中断的写入" : "覆盖这段配置并保存";
        AutomationProperties.SetName(FollowUpButton, FollowUpButton.Content?.ToString() ?? "");
    }
    private async void FollowUp(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        if (model.SyncFollowUp == "recover")
        {
            var server = model.Selected?.Profile ?? throw new InvalidOperationException("请先保存这条连接。");
            model.ShowMessage(await model.RecoverRemoteAsync(server, ct));
            UpdateFollowUp();
            return;
        }
        var profile = ReadEditor();
        string message = await model.SaveAsync(profile, replaceEdited: true, ct);
        if (message.StartsWith("没有同步", StringComparison.Ordinal))
        {
            UpdateFollowUp();
            model.ShowMessage(message);
            return;
        }
        Navigate(HomePage); model.ShowMessage(message);
    });
    private async void DeleteServer(object sender, RoutedEventArgs e)
    {
        if (!deleteArmed || DateTimeOffset.Now - deleteArmedAt < TimeSpan.FromMilliseconds(500))
        {
            deleteArmed = true;
            deleteArmedAt = DateTimeOffset.Now;
            model.ShowMessage("再次点击「删除此连接」以确认。本机连接将被删除，并尝试清除服务器上由 Portkey 写入的代理配置。", onDismiss: () => deleteArmed = false);
            return;
        }
        deleteArmed = false;
        await RunAsync(async ct => { string message = await model.DeleteAsync(ct); Navigate(HomePage); model.ShowMessage(message); });
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
        model.ShowMessage(process.ExitCode == 0 ? "SSH 认证成功。请先保存，再启动连接。" : "SSH 认证未完成。请检查主机指纹、密钥和 ssh-agent。");
    });
    private async void CheckLocalProxy(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        model.ShowMessage(await ProxyHealthChecker.CheckLocalAsync(ReadEditor(), ct));
    });
    private void RefreshLogs(object sender, RoutedEventArgs e) => LogsText.Text = model.Diagnostics();
    private void CopyDiagnostics(object sender, RoutedEventArgs e) { Clipboard.SetText(model.Diagnostics()); model.ShowMessage("诊断信息已复制。分享前请确认其中的服务器名称。"); }
    private void OpenLogFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(model.Logger.DirectoryPath);
        Process.Start(new ProcessStartInfo(model.Logger.DirectoryPath) { UseShellExecute = true });
    }
}
