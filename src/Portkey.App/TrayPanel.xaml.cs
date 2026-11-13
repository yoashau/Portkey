using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly string sshConfigPath;
    private readonly Func<ServerProfile, CancellationToken, Task<ResolvedHost>> resolveHost;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> readDefaultIdentities;
    private CancellationTokenSource? operation;
    private CancellationTokenSource? editorLookup;
    private Guid editId;
    private bool suppressImport;
    private bool updatingEditor;
    private bool importPending;
    private bool editorNeedsLookup;
    private int editorSession;
    private int editorRevision;
    private readonly HashSet<TextBox> inheritedFields = [];
    private readonly Dictionary<TextBox, string> inheritedValues = [];
    private readonly HashSet<TextBox> importTouchedFields = [];
    private readonly Dictionary<TextBox, int> fieldRevisions = [];
    private bool deleteArmed;
    private DateTimeOffset deleteArmedAt;
    private int presentationVersion;
    private (bool Dark, bool Contrast, IntPtr Window)? appliedTheme;
    private static readonly DependencyProperty PageOffsetProperty = DependencyProperty.Register("PageOffset", typeof(double), typeof(TrayPanel), new PropertyMetadata(0.0, (d, e) => ((TrayPanel)d).PageScroll.ScrollToVerticalOffset((double)e.NewValue)));
    private readonly TranslateTransform surfaceTranslation = new();
    public bool IsDismissing { get; private set; }
    public TrayPanel(AppViewModel model) : this(model, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config"), null, null) { }
    internal TrayPanel(AppViewModel model, string sshConfigPath, Func<ServerProfile, CancellationToken, Task<ResolvedHost>>? resolveHost,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? readDefaultIdentities)
    {
        this.model = model;
        this.sshConfigPath = sshConfigPath;
        this.resolveHost = resolveHost ?? model.ConfigReader.ResolveAsync;
        this.readDefaultIdentities = readDefaultIdentities ?? model.ConfigReader.ReadDefaultIdentityFilesAsync;
        InitializeComponent();
        Surface.RenderTransform = surfaceTranslation;
        SurfaceContent.SizeChanged += (_, _) => SurfaceContent.Clip = new RectangleGeometry(new Rect(SurfaceContent.RenderSize), 22, 22);
        DataContext = model;
        ProtocolInput.ItemsSource = Enum.GetValues<ProxyProtocol>();
        ProtocolInput.SelectedIndex = 0;
        PageScroll.PreviewMouseWheel += (_, _) => StopPageScroll();
        PageScroll.PreviewMouseDown += (_, _) => StopPageScroll();
        PageScroll.SizeChanged += (_, _) => UpdatePageScrolling();
        foreach (var box in new[] { UserInput, SshPortInput, IdentityInput })
        {
            box.TextChanged += EditorFieldChanged;
            box.LostKeyboardFocus += (_, _) =>
            {
                if (updatingEditor || editorLookup is not null || HostInput.Text.Length == 0 || (!editorNeedsLookup && box.Text.Length != 0)) return;
                ClearInheritedFields();
                StartEditorLookup(import: false);
            };
        }
        HostInput.TextChanged += EditorHostChanged;
        HostInput.LostKeyboardFocus += (_, _) => { if (!updatingEditor && editorLookup is null && HostInput.Text.Length > 0) StartEditorLookup(import: false); };
        model.SelectionChanged += ClearPlan;
        model.StatusChanged += UpdateEmptyState;
        UpdateEmptyState();
        SourceInitialized += (_, _) =>
        {
            ApplyTheme();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(HandlePanelMouseDown);
        };
        Closing += (_, e) => { e.Cancel = true; Dismiss(); };
    }

    private IntPtr HandlePanelMouseDown(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is not (0x201 or 0x204 or 0x207 or 0x20B) || !GetCursorPos(out var cursor)) return IntPtr.Zero;
        var screen = new Point(cursor.X, cursor.Y);
        if (!new Rect(RenderSize).Contains(PointFromScreen(screen))) return IntPtr.Zero;
        foreach (var box in new[] { ServerSelector, HostAliases, ProtocolInput })
        {
            if (!box.IsDropDownOpen || IsInsideScreenBounds(box, screen)) continue;
            var popup = box.Template.FindName("PART_Popup", box) as Popup;
            if (popup?.Child is FrameworkElement child && child.IsVisible && IsInsideScreenBounds(child, screen)) continue;
            // ComboBox captures the mouse while open. Release it before WPF
            // routes this click so the control under the pointer receives it.
            box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
        }
        return IntPtr.Zero;
    }
    private static bool IsInsideScreenBounds(FrameworkElement element, Point screen) =>
        new Rect(element.RenderSize).Contains(element.PointFromScreen(screen));
    internal void CloseDropDowns()
    {
        foreach (var box in new[] { ServerSelector, HostAliases, ProtocolInput })
            if (box.IsDropDownOpen) box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MousePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out MousePoint point);

    public void ApplyTheme(bool force = false)
    {
        using var registry = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
#pragma warning disable WPF0001 // Match the existing Fluent ThemeMode in App.xaml, including explicit theme previews.
        bool dark = Application.Current.ThemeMode.Equals(ThemeMode.Dark) ||
            (Application.Current.ThemeMode.Equals(ThemeMode.System) && registry?.GetValue("AppsUseLightTheme") is int value && value == 0);
#pragma warning restore WPF0001
        var handle = new WindowInteropHelper(this).Handle;
        var theme = (dark, SystemParameters.HighContrast, handle);
        if (!force && appliedTheme == theme) return;
        appliedTheme = theme;
        NativeWindowStyle.Apply(handle);
        void Brush(string name, string color) => Application.Current.Resources[name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        Brush("PaneBrush", dark ? "#1C1B19" : "#F7F4EF");
        Brush("MenuBrush", dark ? "#2C2A28" : "#FFFBF7");
        Brush("TextBrush", dark ? "#F3EEE7" : "#26231F");
        Brush("SecondaryTextBrush", dark ? "#BDB6AE" : "#706B65");
        Brush("BorderBrush", dark ? "#1AF3EEE7" : "#1C41382D");
        Brush("HoverBrush", dark ? "#17F3EEE7" : "#1448301C");
        Brush("PressedBrush", dark ? "#29F3EEE7" : "#2948301C");
        Brush("FocusBrush", dark ? "#2ED0AE7C" : "#24A47A43");
        Brush("TextSelectionBrush", dark ? "#4D7BA4C7" : "#38638EB4");
        Brush("ControlBrush", dark ? "#0FFFFFFF" : "#B8FFFFFF");
        Brush("FieldHoverBrush", dark ? "#2EFFFFFF" : "#2E48301C");
        Brush("FieldFocusBrush", dark ? "#1CFFFFFF" : "#FAFFFFFF");
        Brush("ThumbBrush", dark ? "#75EBE1D2" : "#704C4236");
        Brush("AccentBrush", dark ? "#71937B" : "#6B8772");
        Brush("PrimaryBrush", dark ? "#71937B" : "#526D59");
        Brush("AccentTextBrush", dark ? "#101A13" : "#FFFFFF");
        Brush("SwitchTrackBrush", dark ? "#484644" : "#D0CCC7");
        Brush("SwitchThumbBrush", dark ? "#F3EEE7" : "#FFFFFF");
        Brush("DangerBrush", dark ? "#DCB69F" : "#92553F");
        Brush("NoticeWarningBrush", dark ? "#DCB69F" : "#92553F");
        if (SystemParameters.HighContrast)
        {
            var resources = Application.Current.Resources;
            resources["PaneBrush"] = resources["MenuBrush"] = SystemColors.WindowBrush;
            resources["TextBrush"] = resources["SecondaryTextBrush"] = SystemColors.WindowTextBrush;
            resources["BorderBrush"] = resources["ThumbBrush"] = SystemColors.WindowTextBrush;
            resources["HoverBrush"] = resources["PressedBrush"] = resources["ControlBrush"] = SystemColors.ControlBrush;
            resources["AccentBrush"] = resources["PrimaryBrush"] = SystemColors.HighlightBrush;
            resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
            resources["NoticeWarningBrush"] = resources["DangerBrush"] = SystemColors.WindowTextBrush;
            resources["FieldHoverBrush"] = SystemColors.WindowTextBrush;
            resources["FieldFocusBrush"] = SystemColors.WindowBrush;
            resources["FocusBrush"] = SystemColors.ControlBrush;
            resources["TextSelectionBrush"] = SystemColors.HighlightBrush;
            resources["SwitchTrackBrush"] = SystemColors.ControlDarkBrush;
            resources["SwitchThumbBrush"] = SystemColors.HighlightTextBrush;
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
        if (page != EditorPage) { editorSession++; editorRevision++; CancelEditorLookup(); }
        foreach (var item in new FrameworkElement[] { HomePage, EditorPage, LogsPage }) item.Visibility = item == page ? Visibility.Visible : Visibility.Collapsed;
        NavigationHeader.Visibility = page == HomePage ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = page == EditorPage ? "连接设置" : "连接日志";
        EditorActions.Visibility = page == EditorPage ? Visibility.Visible : Visibility.Collapsed;
        UpdateFollowUp();
        LogActions.Visibility = page == LogsPage ? Visibility.Visible : Visibility.Collapsed;
        ActionFooter.Visibility = page == HomePage ? Visibility.Collapsed : Visibility.Visible;
        UpdatePageScrolling();
        PageScroll.ScrollToTop();
        model.ClearMessage();
        if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
        {
            // Keep clicked content visible immediately; a short translation supplies continuity.
            PageTranslation.BeginAnimation(TranslateTransform.XProperty, Animation(page == HomePage ? -6 : 6, 0, 100));
        }
    }
    private void UpdatePageScrolling()
    {
        bool home = HomePage.Visibility == Visibility.Visible;
        double minimum = HomePage.MinHeight + HomePage.Margin.Top + HomePage.Margin.Bottom;
        PageScroll.VerticalScrollBarVisibility = home
            ? PageScroll.ActualHeight > 0 && PageScroll.ActualHeight < minimum ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Visible;
    }
    private void BringHelpRowIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (sender is not FrameworkElement help || !ReferenceEquals(e.TargetObject, help) || VisualTreeHelper.GetParent(help) is not FrameworkElement row) return;
        // Scroll the entire labeled row into view, not just the smaller help
        // button; otherwise the switch or label can remain below the footer.
        e.Handled = true;
        row.BringIntoView();
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
    public void AnimateOpen()
    {
        double from = IsVisible ? surfaceTranslation.Y : SlideHeight;
        presentationVersion++;
        IsDismissing = false;
        surfaceTranslation.BeginAnimation(TranslateTransform.YProperty, null);
        bool animate = SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        // Establish the hidden first frame before the animation clock ticks.
        surfaceTranslation.Y = animate ? from : 0;
        if (!animate) return;
        // Only the alpha surface moves; there is no separate native backdrop to expose.
        surfaceTranslation.BeginAnimation(TranslateTransform.YProperty, Animation(from, 0, 250));
    }
    public void Dismiss()
    {
        if (!IsVisible || IsDismissing) return;
        CloseDropDowns();
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
    private void SyncChanged(object sender, RoutedEventArgs e)
    {
        if (SaveButton is not null) SaveButton.Content = importPending ? "正在读取 SSH 配置" : SyncInput.IsChecked == true ? "保存并同步" : "保存";
    }
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
    private void OpenEditor(object sender, RoutedEventArgs e)
    {
        Navigate(EditorPage);
        BeginEditor(model.Selected?.Profile ?? new ServerProfile());
    }
    private void BeginEditor(ServerProfile server)
    {
        int session = ++editorSession;
        editorRevision++;
        CancelEditorLookup();
        editorNeedsLookup = false;
        importTouchedFields.Clear();
        ClearInheritedFields();
        inheritedValues.Clear();
        updatingEditor = true;
        try
        {
            editId = server.Id;
            DeleteButton.IsEnabled = model.Selected?.Profile.Id == server.Id;
            NameInput.Text = server.Name; HostInput.Text = server.Host; UserInput.Text = server.User;
            SshPortInput.Text = server.SshPort?.ToString() ?? ""; IdentityInput.Text = server.IdentityFile;
            LocalPortInput.Text = server.LocalPort.ToString(); RemotePortInput.Text = server.RemotePort.ToString();
            ProtocolInput.SelectedItem = server.Protocol; AutoInput.IsChecked = server.AutoConnect; SyncInput.IsChecked = server.SyncRemoteEnvironment;
            SelectAlias(null);
        }
        finally { updatingEditor = false; }
        SyncChanged(SyncInput, new RoutedEventArgs());
        _ = LoadAliasesAsync(session);
        if (server.Host.Length > 0) StartEditorLookup(import: false);
    }
    private async Task LoadAliasesAsync(int session)
    {
        try
        {
            var aliases = await Task.Run(() => model.ConfigReader.ReadAliases(sshConfigPath));
            await Dispatcher.InvokeAsync(() =>
            {
                if (session != editorSession || EditorPage.Visibility != Visibility.Visible) return;
                suppressImport = true;
                try { HostAliases.ItemsSource = aliases; SelectAlias(HostInput.Text); }
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
        BeginEditor(new ServerProfile());
    }
    private void SelectAlias(string? host)
    {
        bool previous = suppressImport;
        suppressImport = true;
        try { HostAliases.SelectedItem = HostAliases.Items.OfType<string>().FirstOrDefault(x => x.Equals(host, StringComparison.OrdinalIgnoreCase)); }
        finally { suppressImport = previous; }
    }
    private void EditorFieldChanged(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        fieldRevisions[box] = fieldRevisions.GetValueOrDefault(box) + 1;
        if (updatingEditor) return;
        if (inheritedValues.TryGetValue(box, out var value) && box.Text == value) SetInheritedAppearance(box);
        else if (inheritedFields.Remove(box)) ClearInheritedAppearance(box);
        editorNeedsLookup = true;
        if (importPending) importTouchedFields.Add(box);
    }
    private void SetInheritedAppearance(TextBox box)
    {
        inheritedFields.Add(box);
        box.SetResourceReference(Control.ForegroundProperty, "SecondaryTextBrush");
        box.ToolTip = "当前 SSH 配置或默认值；修改后将作为此连接的独立设置。";
        AutomationProperties.SetHelpText(box, (string)box.ToolTip);
    }
    private static void ClearInheritedAppearance(TextBox box)
    {
        box.ClearValue(Control.ForegroundProperty);
        box.ClearValue(ToolTipProperty);
        box.ClearValue(AutomationProperties.HelpTextProperty);
    }
    private void ClearInheritedFields()
    {
        bool previous = updatingEditor;
        updatingEditor = true;
        try
        {
            foreach (var box in inheritedFields.ToArray())
            {
                inheritedFields.Remove(box);
                inheritedValues.Remove(box);
                ClearInheritedAppearance(box);
                box.Clear();
            }
        }
        finally { updatingEditor = previous; }
    }
    private void EditorHostChanged(object sender, TextChangedEventArgs e)
    {
        if (updatingEditor) return;
        editorRevision++;
        CancelEditorLookup();
        editorNeedsLookup = false;
        importTouchedFields.Clear();
        ClearInheritedFields();
        inheritedValues.Clear();
        SelectAlias(HostInput.Text.Trim());
    }
    private void CancelEditorLookup()
    {
        editorLookup?.Cancel();
        editorLookup = null;
        SetImportPending(false);
    }
    private void SetImportPending(bool pending)
    {
        importPending = pending;
        SaveButton.IsEnabled = FollowUpButton.IsEnabled = !pending;
        SyncChanged(SyncInput, new RoutedEventArgs());
    }
    private string EditorValue(TextBox box) => inheritedFields.Contains(box) ? "" : box.Text.Trim();
    private void SetLookupValue(TextBox box, int revision, string? value, bool inherited)
    {
        if (fieldRevisions.GetValueOrDefault(box) != revision || box.Text.Length != 0 || string.IsNullOrWhiteSpace(value)) return;
        bool previous = updatingEditor;
        updatingEditor = true;
        try { box.Text = value; }
        finally { updatingEditor = previous; }
        if (!inherited) return;
        inheritedValues[box] = value;
        SetInheritedAppearance(box);
    }
    private ServerProfile ReadEditor()
    {
        if (ProtocolInput.SelectedItem is not ProxyProtocol protocol) throw new ArgumentException("请选择代理协议。");
        var profile = new ServerProfile
        {
            Id = editId, Name = NameInput.Text.Trim(), Host = HostInput.Text.Trim(), User = EditorValue(UserInput),
            SshPort = ServerProfile.ParseOptionalPort(EditorValue(SshPortInput)), IdentityFile = EditorValue(IdentityInput),
            LocalPort = ServerProfile.ParseRequiredInt(LocalPortInput.Text, "本地代理端口"),
            RemotePort = ServerProfile.ParseRequiredInt(RemotePortInput.Text, "远程监听端口"), Protocol = protocol,
            AutoConnect = AutoInput.IsChecked == true, SyncRemoteEnvironment = SyncInput.IsChecked == true,
            CheckUrl = ServerProfile.FixedCheckUrl, ExpectedStatus = ServerProfile.FixedExpectedStatus, TimeoutSeconds = ServerProfile.FixedTimeoutSeconds
        };
        profile.Validate();
        return profile;
    }
    private void ImportAlias(object sender, SelectionChangedEventArgs e)
    {
        if (suppressImport || HostAliases.SelectedItem is not string alias) return;
        editorRevision++;
        CancelEditorLookup();
        editorNeedsLookup = false;
        importTouchedFields.Clear();
        ClearInheritedFields();
        inheritedValues.Clear();
        updatingEditor = true;
        try
        {
            HostInput.Text = alias; NameInput.Text = alias;
            UserInput.Clear(); SshPortInput.Clear(); IdentityInput.Clear();
        }
        finally { updatingEditor = false; }
        StartEditorLookup(import: true);
    }
    private void StartEditorLookup(bool import)
    {
        if (EditorPage.Visibility != Visibility.Visible || updatingEditor) return;
        CancelEditorLookup();
        string host = HostInput.Text.Trim();
        if (host.Length == 0) return;
        ServerProfile profile;
        try
        {
            profile = new ServerProfile { Name = "SSH preview", Host = host, User = EditorValue(UserInput),
                SshPort = ServerProfile.ParseOptionalPort(EditorValue(SshPortInput)), IdentityFile = EditorValue(IdentityInput) };
            profile.Validate();
        }
        catch (ArgumentException) { return; }
        editorNeedsLookup = false;
        var cancellation = new CancellationTokenSource();
        editorLookup = cancellation;
        SetImportPending(import);
        _ = LoadServerDetailsAsync(profile, import, editorSession, editorRevision,
            fieldRevisions.GetValueOrDefault(UserInput), fieldRevisions.GetValueOrDefault(SshPortInput), fieldRevisions.GetValueOrDefault(IdentityInput), cancellation);
    }
    private async Task LoadServerDetailsAsync(ServerProfile profile, bool import, int session, int revision,
        int userRevision, int portRevision, int identityRevision, CancellationTokenSource cancellation)
    {
        SpecifiedSshSettings? specified = null;
        ResolvedHost? resolved = null;
        bool lookupFailed = false;
        try
        {
            var specifiedTask = Task.Run(() => model.ConfigReader.ReadSpecified(sshConfigPath, profile.Host));
            var resolvedTask = resolveHost(profile, cancellation.Token);
            try { specified = await specifiedTask.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { lookupFailed = true; model.Logger.Write("ssh-config", error.Message); }
            catch (TimeoutException)
            {
                // The config can include an unavailable network path; it must
                // not keep the editor's save action disabled indefinitely.
                _ = specifiedTask.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                lookupFailed = true;
                model.Logger.Write("ssh-config", "读取 SSH 配置超时。");
            }
            try { resolved = await resolvedTask; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { lookupFailed = true; model.Logger.Write("ssh-config", error.Message); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { lookupFailed = true; model.Logger.Write("ssh-config", "读取 SSH 配置超时。"); }
            bool identityConfigured = false;
            string? identity = resolved?.IdentityFiles.FirstOrDefault(x => !x.Equals("none", StringComparison.OrdinalIgnoreCase));
            if (identity is null && resolved is not null && !resolved.IdentityFile.Equals("none", StringComparison.OrdinalIgnoreCase)) identity = resolved.IdentityFile;
            if (profile.IdentityFile.Length == 0 && resolved is { IdentityFiles.Count: > 0 } && !string.IsNullOrWhiteSpace(identity))
            {
                try
                {
                    var defaults = await readDefaultIdentities(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
                    identityConfigured = !resolved.IdentityFiles.SequenceEqual(defaults, StringComparer.Ordinal);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
                { lookupFailed = true; model.Logger.Write("ssh-config", error.Message); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                { lookupFailed = true; model.Logger.Write("ssh-config", "读取 SSH 默认配置超时。"); }
            }
            await Dispatcher.InvokeAsync(() =>
            {
                if (cancellation.IsCancellationRequested || session != editorSession || revision != editorRevision || EditorPage.Visibility != Visibility.Visible) return;
                if (fieldRevisions.GetValueOrDefault(UserInput) != userRevision || fieldRevisions.GetValueOrDefault(SshPortInput) != portRevision || fieldRevisions.GetValueOrDefault(IdentityInput) != identityRevision)
                {
                    if (import) StartEditorLookup(import: true);
                    else
                    {
                        editorNeedsLookup = true;
                        if (!UserInput.IsKeyboardFocusWithin && !SshPortInput.IsKeyboardFocusWithin && !IdentityInput.IsKeyboardFocusWithin)
                        {
                            ClearInheritedFields();
                            StartEditorLookup(import: false);
                        }
                    }
                    return;
                }
                if (import && specified is not null && resolved is not null)
                {
                    // The parser identifies fields written in the config, but
                    // OpenSSH determines their effective precedence and values.
                    if (!importTouchedFields.Contains(UserInput)) SetLookupValue(UserInput, userRevision, specified.User is null ? null : resolved.User, inherited: false);
                    if (!importTouchedFields.Contains(SshPortInput)) SetLookupValue(SshPortInput, portRevision, specified.Port is null ? null : resolved.Port, inherited: false);
                }
                if (import && resolved is not null && identityConfigured && !importTouchedFields.Contains(IdentityInput))
                    SetLookupValue(IdentityInput, identityRevision, identity, inherited: false);
                if (resolved is not null)
                {
                    SetLookupValue(UserInput, userRevision, resolved.User, inherited: true);
                    SetLookupValue(SshPortInput, portRevision, resolved.Port, inherited: true);
                    // ssh -G lists built-in candidates even with no configured
                    // key. A matching default list is not a selected identity.
                    SetLookupValue(IdentityInput, identityRevision, identityConfigured ? identity : null, inherited: true);
                }
                if (lookupFailed) model.ShowMessage("未能完整读取 SSH 配置，请检查显示的信息后再保存。");
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (TaskCanceledException) when (Dispatcher.HasShutdownStarted) { }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                try { await Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(editorLookup, cancellation)) { editorLookup = null; SetImportPending(false); }
                    cancellation.Dispose();
                }); }
                catch (TaskCanceledException) when (Dispatcher.HasShutdownStarted) { cancellation.Dispose(); }
            }
            else cancellation.Dispose();
        }
    }
    private async void SaveServer(object sender, RoutedEventArgs e)
    {
        if (importPending) return;
        await RunAsync(async ct =>
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
    }
    private void UpdateFollowUp()
    {
        if (FollowUpButton is null) return;
        FollowUpButton.Visibility = model.SyncFollowUp is "replace" or "recover" ? Visibility.Visible : Visibility.Collapsed;
        FollowUpButton.Content = model.SyncFollowUp == "recover" ? "恢复中断的写入" : "覆盖这段配置并保存";
        AutomationProperties.SetName(FollowUpButton, FollowUpButton.Content?.ToString() ?? "");
    }
    private async void FollowUp(object sender, RoutedEventArgs e)
    {
        if (importPending) return;
        await RunAsync(async ct =>
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
    }
    private async void DeleteServer(object sender, RoutedEventArgs e)
    {
        if (model.Selected?.Profile.Id != editId) return;
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
