using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using Portkey.App;
using Portkey.Core;
using Portkey.Infrastructure;

#pragma warning disable WPF0001 // Exercise the production Fluent theme at startup and in explicit light/dark modes.

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.Single());
        Directory.CreateDirectory(output);
        string temporary = Path.Combine(Path.GetTempPath(), "portkey-ui-" + Guid.NewGuid());
        // Load the exact compiled App.xaml without running real SSH/settings startup.
        DispatcherOperation? startup = null;
        void Posted(object? sender, DispatcherHookEventArgs e) { if (e.Operation.Priority == DispatcherPriority.Send) startup ??= e.Operation; }
        Dispatcher.CurrentDispatcher.Hooks.OperationPosted += Posted;
        var app = new Portkey.App.App();
        Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= Posted;
        if (startup is null || !startup.Abort()) throw new InvalidOperationException("Unable to suppress production startup in UI fixture.");
        app.InitializeComponent();
        var logger = new DiagnosticLogger(temporary);
        var model = new AppViewModel(new SettingsStore(temporary), logger);
        var profile = new ServerProfile { Name = "example", Host = "example" };
        var connection = new ConnectionViewModel(profile, new NoNetwork(), logger, () => { });
        model.Servers.Add(connection);
        model.Selected = connection;
        var panel = new TrayPanel(model) { Left = 100, Top = 100 };
        var report = new List<string> { "Initial production theme: " + app.ThemeMode };
        Console.WriteLine(report[0]);
        void Check(bool value, string detail)
        {
            if (!value) throw new InvalidOperationException(detail);
            report.Add("PASS " + detail); Console.WriteLine(report[^1]);
        }
        T Named<T>(string name) where T : FrameworkElement => (T)panel.FindName(name);
        bool HomeFullyVisible()
        {
            var logs = Descendants<Button>(panel).Single(x => AutomationProperties.GetName(x) == "连接日志");
            double bottom = logs.TranslatePoint(new Point(0, logs.ActualHeight), panel).Y;
            double top = Named<ComboBox>("ServerSelector").TranslatePoint(new Point(), panel).Y;
            return top >= 0 && bottom <= panel.ActualHeight + 0.5 && Named<ScrollViewer>("PageScroll").ScrollableHeight == 0;
        }
        bool IsInside(DependencyObject child, DependencyObject parent)
        {
            for (var node = child; node is not null; node = VisualTreeHelper.GetParent(node))
                if (ReferenceEquals(node, parent)) return true;
            return false;
        }
        void Pump(int milliseconds = 220)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); panel.UpdateLayout();
        }
        void Click(string label)
        {
            var button = Descendants<Button>(panel).Single(x => AutomationProperties.GetName(x) == label);
            var clock = Stopwatch.StartNew();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            report.Add($"INPUT {label} handler={clock.Elapsed.TotalMilliseconds:F2}ms");
            if (label == "连接设置") Check(Named<StackPanel>("EditorPage").IsVisible && Named<Grid>("Controls").Opacity == 1, "editor is visible immediately without a fade or waiting for SSH config IO");
            Pump();
        }
        void Back() { Named<Button>("BackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
        void Capture(string name, FrameworkElement? root = null)
        {
            var visual = root ?? (FrameworkElement)panel;
            // RenderTargetBitmap renders the WPF surface, not the DWM desktop backdrop.
            // Use the production opaque fallback for these reproducible component renders.
            var pane = ((SolidColorBrush)app.Resources["PaneBrush"]).Color;
            panel.Resources["PaneBrush"] = new SolidColorBrush(Color.FromRgb(pane.R, pane.G, pane.B));
            panel.UpdateLayout();
            foreach (int dpi in new[] { 96, 120, 144, 192 })
            {
                double scale = dpi / 96.0;
                var image = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale), (int)Math.Ceiling(visual.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
                image.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(output, name + "-" + dpi + ".png")); encoder.Save(file);
            }
        }
        void CheckSmoothEdges(string prefix)
        {
            var logo = Named<System.Windows.Shapes.Path>("StatusLogo");
            foreach (int dpi in new[] { 96, 120, 144, 192 })
            {
                double scale = dpi / 96.0;
                var edge = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth * scale), (int)Math.Ceiling(panel.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
                edge.Render(Named<Border>("Surface"));
                var pixels = new byte[edge.PixelWidth * edge.PixelHeight * 4]; edge.CopyPixels(pixels, edge.PixelWidth * 4, 0);
                int radius = (int)Math.Ceiling(22 * scale);
                var coverage = (from y in Enumerable.Range(0, radius) from x in Enumerable.Range(0, radius) select pixels[(y * edge.PixelWidth + x) * 4 + 3]).ToArray();
                Check(coverage[0] == 0 && coverage.Contains((byte)255) && coverage.Count(a => a is > 0 and < 255) >= radius / 2,
                    prefix + " " + dpi + " DPI corners blend through fractional alpha instead of a hard region or opaque rim");
                var icon = new RenderTargetBitmap((int)Math.Ceiling(logo.ActualWidth * scale), (int)Math.Ceiling(logo.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                    context.DrawRectangle(new VisualBrush(logo), null, new Rect(0, 0, logo.ActualWidth, logo.ActualHeight));
                icon.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(icon));
                using (var file = File.Create(Path.Combine(output, prefix + "-link-" + dpi + ".png"))) encoder.Save(file);
                var iconPixels = new byte[icon.PixelWidth * icon.PixelHeight * 4]; icon.CopyPixels(iconPixels, icon.PixelWidth * 4, 0);
                var alpha = Enumerable.Range(0, icon.PixelWidth * icon.PixelHeight).Select(i => iconPixels[i * 4 + 3]).ToArray();
                Check(alpha.Max() >= 200 && alpha.Count(a => a is > 0 and < 255) > 20,
                    prefix + " " + dpi + " DPI link outline retains smooth antialiased curves (max=" + alpha.Max() + ", fractional=" + alpha.Count(a => a is > 0 and < 255) + ")");
            }
        }
        void CheckCorners(Control control, string detail)
        {
            var painted = Descendants<Border>(control).Where(x => x.ActualWidth >= control.ActualWidth - 2 && x.ActualHeight >= control.ActualHeight - 2 && x.Background is SolidColorBrush { Color.A: > 0 }).ToArray();
            Check(painted.Length > 0 && painted.All(x => x.CornerRadius == new CornerRadius(9)), detail);
        }
        try
        {
            using (var tray = new TrayManager(panel, model, () => Task.CompletedTask))
            {
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    // Exercise the shell's version-4 WM_CONTEXTMENU callback before any panel.Show().
                    SendMessageW(tray.Handle, 0x8001, IntPtr.Zero, new IntPtr((1 << 16) | 0x7b));
                    Pump(320);
                    var context = (ContextMenu?)typeof(TrayManager).GetField("menu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tray);
                    Check(context is { IsOpen: true } && PresentationSource.FromVisual(context) is HwndSource source && IsWindowVisible(source.Handle)
                        && context.ActualWidth > 0 && context.ActualHeight > 0 && !panel.IsVisible && !panel.IsLoaded,
                        $"tray right-click {attempt} displays a native menu before the panel has ever been shown");
                    context!.IsOpen = false; Pump();
                }
            }
            foreach (var (name, color) in new[] { ("Portkey", TrayIcon.LightForeground), ("Portkey.light", Colors.White) })
            {
                var decoder = new IconBitmapDecoder(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", name + ".ico")), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
                Check(decoder.Frames.Select(x => x.PixelWidth).Order().SequenceEqual(sizes), name + " application ICO contains all native sizes");
                foreach (var frame in decoder.Frames)
                {
                    int pixels = frame.PixelWidth;
                    var actual = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
                    var expected = TrayIcon.RenderImage(TrayIcon.Link, pixels, color);
                    var actualBytes = new byte[pixels * pixels * 4];
                    var expectedBytes = new byte[actualBytes.Length];
                    actual.CopyPixels(actualBytes, pixels * 4, 0);
                    expected.CopyPixels(expectedBytes, pixels * 4, 0);
                    // PNG stores straight alpha; round-tripping premultiplied RGB can differ by one.
                    bool sameCoverage = Enumerable.Range(0, pixels * pixels).All(i => actualBytes[i * 4 + 3] == expectedBytes[i * 4 + 3]);
                    Check(sameCoverage && actualBytes.Zip(expectedBytes, (a, b) => Math.Abs(a - b)).Max() <= 1, $"{name} application icon {pixels}px matches tray geometry and color within PNG rounding");
                }
            }
            foreach (int pixels in new[] { 16, 20, 24, 32, 40, 48 })
            {
                foreach (var (name, glyph) in new[] { ("portkey", TrayIcon.Link), ("volume", '\uE767'), ("wifi", '\uE701') })
                {
                    var icon = TrayIcon.RenderImage(glyph, pixels, Colors.White);
                    var data = new byte[pixels * pixels * 4]; icon.CopyPixels(data, pixels * 4, 0);
                    var ink = Enumerable.Range(0, pixels * pixels).Where(i => data[i * 4 + 3] >= 128).ToArray();
                    int width = ink.Max(i => i % pixels) - ink.Min(i => i % pixels) + 1;
                    int height = ink.Max(i => i / pixels) - ink.Min(i => i / pixels) + 1;
                    Check(Math.Max(width, height) >= pixels * 0.75, $"native-font {name} {pixels}px uses a full-size glyph footprint ({width}x{height})");
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(icon));
                    using var file = File.Create(Path.Combine(output, $"tray-{name}-{pixels}.png")); encoder.Save(file);
                }
            }
            foreach (var theme in new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark })
            {
                if (!theme.Equals(ThemeMode.System)) app.ThemeMode = theme;
                panel.Resources.Remove("PaneBrush");
                panel.ApplyTheme();
                string prefix = theme.Equals(ThemeMode.System) ? "system" : theme.Equals(ThemeMode.Dark) ? "dark" : "light";
                new WindowInteropHelper(panel).EnsureHandle();
                panel.AnimateOpen(); panel.Show();
                if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
                {
                    var translation = (TranslateTransform)Named<Border>("Surface").RenderTransform;
                    double start = translation.Y;
                    Check(start > panel.Height / 2, prefix + " flyout starts below the visible panel");
                    Check((GetWindowLongPtrW(new WindowInteropHelper(panel).Handle, -20).ToInt64() & 0x80000) != 0, prefix + " native window uses per-pixel alpha for smooth corners");
                    Pump(80);
                    Check(translation.Y > 0 && translation.Y < start, prefix + " alpha surface slides upwards without a separate backdrop");
                }
                Pump(320);
                Check(Named<Border>("NavigationHeader").Visibility == Visibility.Collapsed, prefix + " home has no title bar");
                double homeHeight = panel.ActualHeight;
                Check(Math.Abs(panel.Height - 456) < 0.1 && Math.Abs(panel.MaxHeight - 456) < 0.1, prefix + " panel uses one constant 456 DIP height");
                Check(!Descendants<TextBlock>(Named<Grid>("HomePage")).Any(x => x.Text == "Portkey"), prefix + " home has no branding header");
                Check(Named<Grid>("SurfaceContent").Clip is RectangleGeometry { RadiusX: 22, RadiusY: 22 }, prefix + " child content is clipped separately from the antialiased surface rim");
                CheckSmoothEdges(prefix);
                if (Environment.OSVersion.Version.Build >= 22621)
                {
                    var handle = new WindowInteropHelper(panel).Handle;
                    Check(DwmGetWindowAttribute(handle, 33, out int rounding, 4) == 0 && rounding == 1
                        && DwmGetWindowAttribute(handle, 38, out int backdrop, 4) == 0 && backdrop == 1,
                        prefix + " native rounding and backdrop cannot paint a second rim");
                }
                Check(Named<ScrollViewer>("PageScroll").VerticalScrollBarVisibility == ScrollBarVisibility.Disabled, prefix + " home does not show a scrollbar");
                Check(Named<ScrollViewer>("PageScroll").ScrollableHeight == 0, prefix + " home fits without scrolling");
                Check(HomeFullyVisible(), prefix + " home content stays fully visible");
                var flow = Named<Viewbox>("PortFlow");
                bool FlowFits()
                {
                    var local = Named<StackPanel>("LocalEndpoint");
                    var remote = Named<StackPanel>("RemoteEndpoint");
                    double flowLeft = flow.TranslatePoint(new Point(), panel).X;
                    return local.TranslatePoint(new Point(local.ActualWidth, 0), panel).X <= flowLeft + 0.5
                        && flowLeft + flow.ActualWidth <= remote.TranslatePoint(new Point(), panel).X + 0.5;
                }
                var strands = Descendants<System.Windows.Shapes.Path>(flow).ToArray();
                Check(flow.IsVisible && FlowFits() && strands.Length == 2,
                    prefix + " port connection shows two curves between the endpoint labels");
                bool StrandsMeet(double fraction)
                {
                    strands[0].Data.GetFlattenedPathGeometry().GetPointAtFractionLength(fraction, out var first, out _);
                    strands[1].Data.GetFlattenedPathGeometry().GetPointAtFractionLength(fraction, out var second, out _);
                    return (first - second).Length < 1;
                }
                Check(StrandsMeet(0) && StrandsMeet(0.5) && StrandsMeet(1),
                    prefix + " port curves meet at both ends and cross in the middle");
                foreach (int dpi in new[] { 96, 120, 144, 192 })
                {
                    double scale = dpi / 96.0;
                    var image = new RenderTargetBitmap((int)Math.Ceiling(flow.ActualWidth * scale), (int)Math.Ceiling(flow.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
                    var drawing = new DrawingVisual();
                    using (var context = drawing.RenderOpen())
                        context.DrawRectangle(new VisualBrush(flow), null, new Rect(0, 0, flow.ActualWidth, flow.ActualHeight));
                    image.Render(drawing);
                    var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
                    image.CopyPixels(pixels, image.PixelWidth * 4, 0);
                    int fractional = Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] is > 0 and < 255);
                    Check(fractional > 20, prefix + " " + dpi + " DPI port curves have antialiased edges (fractional=" + fractional + ")");
                }
                panel.Width = 300; Pump();
                Check(FlowFits(), prefix + " port curves remain between the labels in a narrow window");
                panel.Width = 380; Pump();
                var repeated = new ConnectionViewModel(new ServerProfile { Name = "lycoris", Host = "lycoris", User = "lycoris" }, new NoNetwork(), logger, () => { });
                var distinct = new ConnectionViewModel(new ServerProfile { Name = "工作机", Host = "lycoris", User = "lycoris", RemotePort = 17891 }, new NoNetwork(), logger, () => { });
                Check(repeated.EndpointLabel.Length == 0 && distinct.EndpointLabel == "lycoris@lycoris", prefix + " home repeats an SSH address only when the host differs from the server name");
                model.Servers.Add(distinct); model.Selected = distinct; Pump();
                Check(Descendants<TextBlock>(Named<Grid>("ConnectionCard")).Any(x => x.IsVisible && x.Text == "lycoris@lycoris") && HomeFullyVisible(), prefix + " a different SSH host stays visible without making home scroll");
                model.Selected = connection; model.Servers.Remove(distinct); Pump();
                double homeViewport = Named<ScrollViewer>("PageScroll").ActualHeight;
                model.ShowMessage("没有同步服务器配置，尚未生效。新终端仍使用旧地址，因此可能无法联网。");
                Pump();
                var notice = Named<Border>("Notice");
                Check(notice.IsVisible && !IsInside(notice, Named<ScrollViewer>("PageScroll")) && Math.Abs(Named<ScrollViewer>("PageScroll").ActualHeight - homeViewport) < 1 && HomeFullyVisible(), prefix + " notice overlays the home page without shrinking it or enabling scroll");
                double noticeTop = notice.TranslatePoint(new Point(), panel).Y;
                double noticeBottom = noticeTop + notice.ActualHeight;
                double portBottom = Descendants<TextBlock>(Named<Grid>("ConnectionCard")).First(x => x.Text == profile.RemotePort.ToString()).TranslatePoint(new Point(0, Descendants<TextBlock>(Named<Grid>("ConnectionCard")).First(x => x.Text == profile.RemotePort.ToString()).ActualHeight), panel).Y;
                double buttonTop = Named<Button>("ToggleButton").TranslatePoint(new Point(), panel).Y;
                Check(noticeTop >= portBottom - 4 && noticeBottom <= buttonTop - 2 && !Descendants<Button>(notice).Any(), prefix + $" home notice is a sentence at a fixed position (ports={portBottom:0.#} notice={noticeTop:0.#}-{noticeBottom:0.#} button={buttonTop:0.#})");
                double fixedNoticeTop = noticeTop;
                model.SetSyncWarning("没有同步服务器配置，尚未生效。新终端仍使用旧地址，因此可能无法联网。");
                model.ClearMessage();
                Pump();
                Check(notice.IsVisible && !Descendants<TextBlock>(Named<Grid>("ConnectionCard")).Any(x => x.IsVisible && x.Text.Contains("没有同步")) && HomeFullyVisible(), prefix + " persistent sync warning uses the overlay instead of expanding the status card");
                if (prefix == "light") Capture(prefix + "-notice");
                model.SetSyncWarning("");
                Pump();
                CheckCorners(Named<ComboBox>("ServerSelector"), prefix + " server selector has visibly rounded painted corners");
                var selector = Named<ComboBox>("ServerSelector");
                Check(Descendants<TextBlock>(selector).Any(x => x.Text == profile.Name) && !Descendants<TextBlock>(selector).Any(x => x.Text.Contains("ConnectionViewModel")), prefix + " server selector renders its display name through the native item template selector");
                selector.IsDropDownOpen = true; Pump();
                Check(Descendants<Border>(((Popup)selector.Template.FindName("PART_Popup", selector)).Child).Any(x => x.CornerRadius.TopLeft >= 8), prefix + " server dropdown retains rounded corners");
                selector.IsDropDownOpen = false; Pump();
                var toggle = Named<Button>("ToggleButton");
                var reconnect = Named<Button>("ReconnectButton");
                Check(Descendants<Border>(toggle).All(x => x.CornerRadius == new CornerRadius(9)) && Descendants<Border>(reconnect).All(x => x.CornerRadius == new CornerRadius(9)), prefix + " action buttons use matching rounded normal, hover, pressed and focus layers");
                Check(toggle.ActualHeight == 44 && reconnect.ActualHeight == 44 && Math.Abs(toggle.TranslatePoint(new Point(), panel).Y - reconnect.TranslatePoint(new Point(), panel).Y) < 0.1, prefix + " connect and reconnect buttons share 44 DIP height and alignment");
                Check(Descendants<TextBlock>(Named<Grid>("ConnectionCard")).Any(x => x.Text == profile.RemotePort.ToString()) && connection.EndpointLabel.Length == 0 && !Descendants<TextBlock>(Named<Grid>("ConnectionCard")).Any(x => x.IsVisible && x.Text == profile.Name), prefix + " home shows real ports without repeating the selected server name");
                foreach (var label in Descendants<TextBlock>(Named<Grid>("ConnectionCard")).Where(x => x.Text == "本地代理" || x.Text == "已暂停"))
                    report.Add("FONT " + label.Text + " family=" + label.FontFamily.Source + " faces=" + string.Join(",", GlyphFaces(VisualTreeHelper.GetDrawing(label))));
                foreach (var text in Descendants<TextBlock>(panel).Where(x => x.IsVisible && x.Text.Length > 0))
                {
                    Check(OutlineTextRendering.IsActive(text) && OutlineTextRendering.PaintedGlyphs(text) > 0, prefix + " outline renderer paints visible text: " + text.Text);
                    var peer = new TextBlockAutomationPeer(text);
                    Check(peer.GetName() == text.Text && !peer.IsOffscreen(), prefix + " outline text retains its accessible name and visibility: " + text.Text);
                }
                // A higher-z opaque surface must cover outlined text just like
                // native text. Notices use this same layering over page content.
                var homeStatus = Descendants<TextBlock>(Named<Grid>("ConnectionCard")).First(x => x.Text == connection.Label);
                var surfaceGrid = Named<Grid>("SurfaceContent");
                var origin = homeStatus.TranslatePoint(new Point(), surfaceGrid);
                var cover = new Border { Background = new SolidColorBrush(Color.FromRgb(240, 64, 32)), Width = Math.Ceiling(homeStatus.ActualWidth) + 4,
                    Height = Math.Ceiling(homeStatus.ActualHeight) + 4, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(origin.X - 2, origin.Y - 2, 0, 0) };
                Grid.SetRowSpan(cover, 3); Panel.SetZIndex(cover, 5); surfaceGrid.Children.Add(cover); Pump();
                var coverImage = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                coverImage.Render(panel); var coverPixels = new byte[coverImage.PixelWidth * coverImage.PixelHeight * 4]; coverImage.CopyPixels(coverPixels, coverImage.PixelWidth * 4, 0);
                var coverOrigin = cover.TranslatePoint(new Point(), panel); int leaked = 0;
                for (int y = (int)Math.Ceiling(coverOrigin.Y) + 2; y < (int)(coverOrigin.Y + cover.ActualHeight) - 2; y++)
                    for (int x = (int)Math.Ceiling(coverOrigin.X) + 2; x < (int)(coverOrigin.X + cover.ActualWidth) - 2; x++)
                    { int index = (y * coverImage.PixelWidth + x) * 4; if (Math.Abs(coverPixels[index] - 32) > 3 || Math.Abs(coverPixels[index + 1] - 64) > 3 || Math.Abs(coverPixels[index + 2] - 240) > 3) leaked++; }
                surfaceGrid.Children.Remove(cover); Pump();
                Check(leaked == 0, prefix + " higher-z surfaces completely cover underlying outline text (leaked=" + leaked + ")");
                Capture(prefix + "-home");
                Click("连接设置");
                model.ShowMessage("正在处理");
                Pump();
                var editorNotice = Named<Border>("Notice");
                Check(editorNotice.IsVisible && Math.Abs(editorNotice.TranslatePoint(new Point(), panel).Y - fixedNoticeTop) < 1 && Descendants<TextBlock>(editorNotice).Any(x => x.Text == "正在处理") && !Descendants<Button>(editorNotice).Any(x => (x.Content?.ToString() ?? "").Contains("取消")), prefix + " processing notice stays in the same position and has no cancel action");
                model.ClearMessage();
                Pump();
                CheckCorners(Named<TextBox>("NameInput"), prefix + " editable text field has rounded painted corners");
                Named<TextBox>("NameInput").Focus(); Pump();
                CheckCorners(Named<TextBox>("NameInput"), prefix + " focused text field retains rounded painted corners");
                CheckCorners(Named<ComboBox>("HostAliases"), prefix + " alias selector has rounded painted corners");
                var editor = Named<StackPanel>("EditorPage");
                Check(!Descendants<Expander>(editor).Any() && panel.FindName("HealthOptions") is null && panel.FindName("CheckUrlInput") is null && panel.FindName("StatusInput") is null && panel.FindName("TimeoutInput") is null, prefix + " health check is fixed and has no custom form");
                Check(Descendants<TextBlock>(editor).Count(x => x.Text == "可选") == 3, prefix + " user, SSH port and identity are marked optional");
                Check(!Descendants<TextBlock>(editor).Any(x => (x.Text ?? "").Contains("不要填 SSH 的 22") || (x.Text ?? "").Contains("已经打开的终端不会变")), prefix + " long explanations are not inline");
                foreach (var help in new[] { "SSH 主机说明", "SSH 用户说明", "SSH 端口说明", "私钥路径说明", "本地代理端口说明", "服务器端口说明", "代理协议说明", "启动时自动连接说明", "新终端代理说明" })
                {
                    var mark = Descendants<ContentControl>(editor).Single(x => AutomationProperties.GetName(x) == help);
                    Check(mark.Focusable && mark.IsTabStop && ToolTipService.GetShowsToolTipOnKeyboardFocus(mark) == true && mark.ToolTip is string { Length: > 12 } && mark.ActualHeight <= 18 && mark.ActualWidth <= 18 && Descendants<TextBlock>(mark).Any(x => x.Text == "?"), prefix + " " + help + " supports compact hover and keyboard focus help");
                    var helpPeer = UIElementAutomationPeer.CreatePeerForElement(mark);
                    Check(helpPeer is not null && helpPeer.GetName() == help && helpPeer.GetHelpText() == mark.ToolTip as string, prefix + " " + help + " exposes both its name and explanation to screen readers");
                }
                // Inspect a real tooltip in each production theme, including
                // foreground/background contrast and rendered glyph color.
                var hostHelp = Descendants<ContentControl>(editor).Single(x => AutomationProperties.GetName(x) == "SSH 主机说明");
                var helpTip = new ToolTip { Content = hostHelp.ToolTip, PlacementTarget = hostHelp, Placement = PlacementMode.Bottom };
                try
                {
                    helpTip.IsOpen = true; Pump();
                    var tipText = Descendants<TextBlock>(helpTip).First(x => x.IsVisible && x.Text.Length > 0);
                    var foreground = ((SolidColorBrush)helpTip.Foreground).Color;
                    var background = ((SolidColorBrush)helpTip.Background).Color;
                    double Channel(byte value) { double c = value / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
                    double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
                    double first = Luminance(foreground), second = Luminance(background);
                    double contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
                    Check(contrast >= 4.5 && OutlineTextRendering.PaintedGlyphs(tipText) > 0, prefix + " actual tooltip text is painted with readable theme contrast (ratio=" + contrast.ToString("F1") + ")");
                    Capture(prefix + "-tooltip", helpTip);
                }
                finally { helpTip.IsOpen = false; Pump(); }
                Check(Named<TextBox>("RemotePortInput").TranslatePoint(new Point(0, Named<TextBox>("RemotePortInput").ActualHeight), panel).Y < Named<ComboBox>("ProtocolInput").TranslatePoint(new Point(), panel).Y
                    && Named<ComboBox>("ProtocolInput").TranslatePoint(new Point(), panel).Y < Named<CheckBox>("AutoInput").TranslatePoint(new Point(), panel).Y
                    && Named<CheckBox>("AutoInput").TranslatePoint(new Point(), panel).Y < Named<CheckBox>("SyncInput").TranslatePoint(new Point(), panel).Y,
                    prefix + " protocol and automatic options are grouped below the address fields");
                foreach (var box in new[] { Named<CheckBox>("AutoInput"), Named<CheckBox>("SyncInput") })
                {
                    box.ApplyTemplate();
                    var mark = (FrameworkElement)box.Template.FindName("Box", box);
                    var label = Named<TextBlock>(box.Name == "AutoInput" ? "AutoLabel" : "SyncLabel");
                    var content = (FrameworkElement)label;
                    double markCenter = mark.TranslatePoint(new Point(0, mark.ActualHeight / 2), panel).Y;
                    double textCenter = content.TranslatePoint(new Point(0, content.ActualHeight / 2), panel).Y;

                    Check(box.FontSize == 15 && label.FontSize == 15 && label.TextWrapping == TextWrapping.NoWrap && Math.Abs(markCenter - textCenter) < 1, prefix + " " + box.Name + " label is 15 DIP and centered on its box");
                    var helpName = box.Name == "AutoInput" ? "启动时自动连接说明" : "新终端代理说明";
                    var help = Descendants<ContentControl>(editor).Single(x => AutomationProperties.GetName(x) == helpName);
                    Check(!IsInside(help, box), prefix + " " + box.Name + " help is outside the switch's click and keyboard activation route");
                    var page = Named<ScrollViewer>("PageScroll");
                    page.ScrollToTop(); Pump(); help.BringIntoView(); Pump();
                    var helpRow = (FrameworkElement)VisualTreeHelper.GetParent(help);
                    double rowTop = helpRow.TranslatePoint(new Point(), page).Y;
                    Check(rowTop >= -0.5 && rowTop + helpRow.ActualHeight <= page.ViewportHeight + 0.5,
                        prefix + " " + box.Name + " keyboard help reveals the complete switch row (top=" + rowTop.ToString("F1") + ", bottom=" + (rowTop + helpRow.ActualHeight).ToString("F1") + ", viewport=" + page.ViewportHeight.ToString("F1") + ")");
                    box.BringIntoView(); Pump();
                    var center = label.TranslatePoint(new Point(label.ActualWidth / 2, label.ActualHeight / 2), panel);
                    var hit = panel.InputHitTest(center) as DependencyObject;
                    Check(hit is not null && IsInside(hit, box), prefix + " " + box.Name + " label remains part of its clickable switch");
                }
                foreach (var field in Descendants<TextBox>(Named<StackPanel>("EditorPage")))
                    CheckCorners(field, prefix + " " + field.Name + " uses rounded painted input corners");
                var nameInput = Named<TextBox>("NameInput");
                nameInput.Focus(); nameInput.SelectAll(); nameInput.SelectedText = "工作服务器"; nameInput.Undo();
                Check(nameInput.Text == profile.Name, prefix + " styled text input preserves selection, editing and undo");
                Pump();
                Check(OutlineTextRendering.IsActive(nameInput) && OutlineTextRendering.PaintedGlyphs(nameInput) > 0, prefix + " text editing paints actual outline glyphs with native editing behavior");
                var inputPeer = new TextBoxAutomationPeer(nameInput);
                Check(((IValueProvider)inputPeer.GetPattern(PatternInterface.Value)).Value == profile.Name, prefix + " input value remains accessible to UI Automation");
                // Check the actual selection fill, not just SelectedText. Spaces
                // expose the background without a glyph covering the sample.
                nameInput.Text = "        "; nameInput.Focus(); nameInput.Select(0, 0); Pump();
                var left = nameInput.GetRectFromCharacterIndex(2, false);
                var right = nameInput.GetRectFromCharacterIndex(3, false);
                var sample = nameInput.TranslatePoint(new Point((left.X + right.X) / 2, left.Y + left.Height / 2), panel);
                Color SelectionPixel()
                {
                    var image = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    image.Render(panel);
                    var pixel = new byte[4]; image.CopyPixels(new Int32Rect((int)sample.X, (int)sample.Y, 1, 1), pixel, 4, 0);
                    return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
                }
                var unselected = SelectionPixel(); nameInput.SelectAll(); Pump(); var selected = SelectionPixel();
                int selectionDifference = Math.Max(Math.Abs(unselected.R - selected.R), Math.Max(Math.Abs(unselected.G - selected.G), Math.Abs(unselected.B - selected.B)));
                Check(selectionDifference >= 20, prefix + " selected input has clearly visible fill (channel difference=" + selectionDifference + ")");
                nameInput.Text = profile.Name; nameInput.SelectAll(); Pump(); Capture(prefix + "-editor-selected");
                Check(nameInput.SelectedText == profile.Name, prefix + " outline text retains native selection");
                nameInput.Select(nameInput.Text.Length, 0);
                var sync = Named<CheckBox>("SyncInput");
                var switchPeer = (IToggleProvider)new CheckBoxAutomationPeer(sync).GetPattern(PatternInterface.Toggle);
                switchPeer.Toggle(); Pump();
                Check(sync.IsChecked == false && Equals(Named<Button>("SaveButton").Content, "保存"), prefix + " switch automation updates the actual save behavior");
                switchPeer.Toggle(); Pump();
                Check(sync.IsChecked == true && Equals(Named<Button>("SaveButton").Content, "保存并同步"), prefix + " switch automation restores synchronization");
                CheckCorners(Named<ComboBox>("ProtocolInput"), prefix + " protocol selector uses rounded painted corners");
                var protocol = Named<ComboBox>("ProtocolInput");
                var expandPeer = (IExpandCollapseProvider)new ComboBoxAutomationPeer(protocol).GetPattern(PatternInterface.ExpandCollapse);
                expandPeer.Expand(); Pump(120);
                Check(protocol.IsDropDownOpen && expandPeer.ExpandCollapseState == ExpandCollapseState.Expanded, prefix + " protocol dropdown retains native accessibility expand behavior");
                protocol.SelectedItem = ProxyProtocol.Socks5; Pump();
                Check(protocol.SelectedItem is ProxyProtocol.Socks5, prefix + " protocol dropdown retains native selection behavior");
                expandPeer.Collapse(); protocol.SelectedItem = ProxyProtocol.Mixed; Pump();
                var scroll = Named<ScrollViewer>("PageScroll");
                var back = Named<Button>("BackButton");
                var save = Named<Button>("SaveButton");
                double backY = back.TranslatePoint(new Point(), panel).Y;
                double saveY = save.TranslatePoint(new Point(), panel).Y;
                Check(scroll.ScrollableHeight > 0 && scroll.ViewportHeight > 100, prefix + " long form scrolls within bounded viewport");
                scroll.ScrollToEnd(); Pump();
                Check(scroll.VerticalOffset > 0 && Math.Abs(back.TranslatePoint(new Point(), panel).Y - backY) < 1, prefix + " back remains fixed after scrolling to bottom");
                Check(Math.Abs(save.TranslatePoint(new Point(), panel).Y - saveY) < 1 && saveY + save.ActualHeight <= panel.ActualHeight, prefix + " save remains visible at fixed footer");
                var bar = Descendants<ScrollBar>(scroll).First(x => x.Orientation == Orientation.Vertical && x.IsVisible);
                Check(bar.TranslatePoint(new Point(bar.ActualWidth, 0), panel).X >= panel.ActualWidth - 3, prefix + " scrollbar reaches the right edge");
                var track = (Track)bar.Template.FindName("PART_Track", bar);
                var grip = (Border)track.Thumb.Template.FindName("Grip", track.Thumb);
                Check(grip.Width <= 4, prefix + " scrollbar has a 2 DIP resting grip and at most 4 DIP hover grip");
                Check(Math.Abs(panel.ActualHeight - homeHeight) < 1, prefix + " editor keeps home container height");
                Capture(prefix + "-editor-bottom");
                scroll.ScrollToTop(); Pump(); Capture(prefix + "-editor");
                scroll.ScrollToVerticalOffset(Math.Min(120, scroll.ScrollableHeight)); Pump();
                double manualOffset = scroll.VerticalOffset;
                scroll.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent }); Pump();
                Check(Math.Abs(scroll.VerticalOffset - manualOffset) < 1, prefix + " manual interaction cancels automatic scrolling without jumping backwards");
                Back();
                Click("连接设置");
                Check(!Descendants<Button>(panel).Any(x => AutomationProperties.GetName(x) == "服务器配置"), prefix + " server setup is not a separate page");
                Check(Named<CheckBox>("SyncInput").IsChecked == true && Named<Button>("FollowUpButton").Visibility != Visibility.Visible, prefix + " sync is on by default and overwrite stays hidden until sync is blocked");
                Check(Math.Abs(panel.Height - 456) < 0.1 && Math.Abs(panel.ActualHeight - homeHeight) < 1, prefix + " editor returns to the same constant 456 DIP panel");
                Back(); Click("连接日志");
                Named<TextBox>("LogsText").Text = string.Join('\n', Enumerable.Range(0, 40).Select(x => $"20:05:{x:00} [UI fixture] 布局验证日志，不代表网络测试结果"));
                Pump(); Check(Math.Abs(panel.ActualHeight - homeHeight) < 1 && scroll.ScrollableHeight == 0, prefix + " logs fill the fixed-height container without nested page scrolling"); Capture(prefix + "-logs"); Back();
                panel.Dismiss(); panel.AnimateOpen(); Pump(320);
                Check(panel.IsVisible && Math.Abs(Named<Border>("Surface").Opacity - 1) < 0.01, prefix + " reopening cancels a pending dismiss animation");
                panel.Dismiss();
                if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
                {
                    Pump(80);
                    Check(((TranslateTransform)Named<Border>("Surface").RenderTransform).Y > 0, prefix + " dismissal slides down towards the taskbar");
                }
                Pump(); Check(!panel.IsVisible, prefix + " dismiss animation ends with a hidden window");
            }
            using (var tray = new TrayManager(panel, model, () => Task.CompletedTask))
            {
                NativeRect first = default;
                void FirstVisible(object sender, DependencyPropertyChangedEventArgs e)
                {
                    if (panel.IsVisible) GetWindowRect(new WindowInteropHelper(panel).Handle, out first);
                }
                panel.IsVisibleChanged += FirstVisible;
                try
                {
                    tray.Show(); Pump(320);
                    var originalIcon = typeof(TrayManager).GetField("icon", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tray);
                    var originalTextBrush = app.Resources["TextBrush"];
                    typeof(TrayManager).GetMethod("RefreshStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(tray, null);
                    panel.ApplyTheme();
                    Check(Equals(originalIcon, typeof(TrayManager).GetField("icon", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tray)) && ReferenceEquals(originalTextBrush, app.Resources["TextBrush"]), "status updates and unchanged theme reuse the icon and brush resources");
                    GetWindowRect(new WindowInteropHelper(panel).Handle, out var last);
                    Check(first.Right > first.Left && first.Equals(last), "tray opening has final native bounds before its first visible frame");
                    foreach (var theme in new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark })
                    {
                        app.ThemeMode = theme;
                        typeof(TrayManager).GetMethod("ShowMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(tray, null);
                        Pump(320);
                        var context = (ContextMenu)typeof(TrayManager).GetField("menu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tray)!;
                        Check(!context.Items.OfType<MenuItem>().Any(x => Equals(x.Header, "设置")), theme + " tray menu has no removed startup settings entry");
                        Check(context.IsOpen && context.FontSize == 15 && context.FontWeight == FontWeights.Normal && context.Background is SolidColorBrush { Color.A: 255 }, theme + " tray context menu uses matching typography and a readable surface");
                        GetWindowRect(((HwndSource)PresentationSource.FromVisual(context)).Handle, out var menuBounds);
                        Check(context.Placement == PlacementMode.Custom && Math.Abs(menuBounds.Bottom - last.Bottom) <= 1, theme + " tray menu has the same physical taskbar gap as the panel");
                        Check(((Border)context.Template.FindName("Border", context)).CornerRadius == new CornerRadius(12) && context.Items.OfType<MenuItem>().All(x => ((Border)x.Template.FindName("ItemBorder", x)).CornerRadius == new CornerRadius(9) && x.ActualHeight >= 40), theme + " tray menu container and every action have matching rounded corners");
                        var menuText = Descendants<TextBlock>(context).First(x => x.IsVisible && x.Text.Length > 0);
                        var expectedInk = ((SolidColorBrush)menuText.Foreground).Color;
                        var renderedMenu = new RenderTargetBitmap((int)Math.Ceiling(context.ActualWidth), (int)Math.Ceiling(context.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        renderedMenu.Render(context);
                        var menuPixels = new byte[renderedMenu.PixelWidth * renderedMenu.PixelHeight * 4];
                        renderedMenu.CopyPixels(menuPixels, renderedMenu.PixelWidth * 4, 0);
                        var textOrigin = menuText.TranslatePoint(new Point(), context);
                        int inkDifference = 255;
                        for (int y = Math.Max(0, (int)Math.Floor(textOrigin.Y)); y < Math.Min(renderedMenu.PixelHeight, (int)Math.Ceiling(textOrigin.Y + menuText.ActualHeight)); y++)
                            for (int x = Math.Max(0, (int)Math.Floor(textOrigin.X)); x < Math.Min(renderedMenu.PixelWidth, (int)Math.Ceiling(textOrigin.X + menuText.ActualWidth)); x++)
                            {
                                int index = (y * renderedMenu.PixelWidth + x) * 4;
                                if (menuPixels[index + 3] < 250) continue;
                                int difference = Math.Max(Math.Abs(menuPixels[index] - expectedInk.B), Math.Max(Math.Abs(menuPixels[index + 1] - expectedInk.G), Math.Abs(menuPixels[index + 2] - expectedInk.R)));
                                inkDifference = Math.Min(inkDifference, difference);
                            }
                        Check(inkDifference <= 4 && context.Items.OfType<MenuItem>().All(x => x.IsEnabled), theme + " enabled menu text reaches its actual foreground color without a second opacity multiplier (difference=" + inkDifference + ")");
                        Capture(theme.ToString().ToLowerInvariant() + "-tray-menu", context);
                        context.IsOpen = false; Pump();
                    }
                }
                finally { panel.IsVisibleChanged -= FirstVisible; panel.Hide(); }
            }
            Named<Button>("BackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            panel.MaxHeight = 300;
            panel.AnimateOpen(); panel.Show(); Pump(320);
            var limitedScroll = Named<ScrollViewer>("PageScroll");
            Check(limitedScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && limitedScroll.ScrollableHeight > 0, "short work areas make the home page scroll instead of clipping its actions");
            limitedScroll.ScrollToEnd(); Pump();
            var logsAction = Descendants<Button>(panel).Single(x => AutomationProperties.GetName(x) == "连接日志");
            Check(logsAction.TranslatePoint(new Point(0, logsAction.ActualHeight), panel).Y <= panel.ActualHeight + 0.5, "home navigation remains reachable at a constrained 300 DIP height");
            Capture("constrained-home");
            using (var restoredTray = new TrayManager(panel, model, () => Task.CompletedTask))
            {
                restoredTray.Show(); limitedScroll.ScrollToTop(); Pump();
                Check(panel.Height == 456 && limitedScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled && limitedScroll.ScrollableHeight == 0, "reopening from a short work area restores the normal height and removes home scrolling");
            }
            int stalePlacement = 0;
            void CountPlacement(string line) { if (line.Contains("[placement]", StringComparison.Ordinal)) stalePlacement++; }
            logger.Written += CountPlacement;
            try { panel.Width = 370; Pump(); panel.Width = 380; Pump(); }
            finally { logger.Written -= CountPlacement; }
            Check(stalePlacement == 0, "disposed tray controllers cannot reposition the panel through stale window event handlers");
            model.Servers.Clear();
            typeof(TrayPanel).GetMethod("UpdateEmptyState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, null);
            Check(Named<TextBlock>("EmptyText").IsVisible && !Named<ComboBox>("ServerSelector").IsVisible && !Named<Grid>("ConnectionActions").IsVisible, "empty state hides server-specific controls");
            Check(Named<Button>("AddServerButton").IsVisible && !Descendants<Button>(panel).Any(x => AutomationProperties.GetName(x) == "服务器配置"), "empty home provides a primary add action and has no separate server page");
            Check(Named<Border>("EmptyState").BorderThickness == new Thickness(0) && !Descendants<Button>(panel).Any(x => AutomationProperties.GetName(x) == "启动设置"), "empty home has no large framed box or Windows startup navigation");
            Capture("dark-empty");
            Click("添加服务器");
            Check(Named<StackPanel>("EditorPage").IsVisible && Named<TextBox>("NameInput").Text == "新服务器" && Named<TextBox>("HostInput").Text == "", "empty-home add action opens a fresh editable connection");
            panel.Hide();
            var comparison = new Window { Title = "Portkey font comparison fixture", Width = 260, Height = 120, Left = 100, Top = 100, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = new SolidColorBrush(Color.FromRgb(247, 244, 239)), ShowInTaskbar = false, Topmost = true, UseLayoutRounding = true, FontFamily = (FontFamily)app.Resources["InterfaceFont"] };
            TextOptions.SetTextFormattingMode(comparison, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(comparison, TextRenderingMode.Grayscale);
            var rows = new Grid(); rows.RowDefinitions.Add(new RowDefinition()); rows.RowDefinitions.Add(new RowDefinition());
            var raster = new TextBlock { Text = "代理正常", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(38, 35, 31)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
            var outline = new TextBlock { Text = raster.Text, FontSize = raster.FontSize, FontWeight = raster.FontWeight, Foreground = raster.Foreground, VerticalAlignment = raster.VerticalAlignment, Margin = raster.Margin };
            OutlineTextRendering.SetEnabled(raster, false); OutlineTextRendering.SetEnabled(outline, true);
            Grid.SetRow(outline, 1); rows.Children.Add(raster); rows.Children.Add(outline); comparison.Content = rows;
            comparison.Show(); Pump();
            Check(OutlineTextRendering.PaintedGlyphs(outline) == 4 && new TextBlockAutomationPeer(outline).GetName() == "代理正常", "comparison fixture draws all four title glyphs without replacing their accessible text");
            Capture("font-raster-vs-outline", comparison);
            // Same glyphs, advances, size and color. Compare the two rows at each
            // DPI; the test must demonstrate changed pixel coverage, not just flags.
            foreach (int dpi in new[] { 96, 120, 144, 192 })
            {
                double scale = dpi / 96.0;
                var bitmap = new RenderTargetBitmap((int)(comparison.ActualWidth * scale), (int)(comparison.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32); bitmap.Render(comparison);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                int half = bitmap.PixelHeight / 2, offset = half * bitmap.PixelWidth * 4;
                int difference = 0, nativeEdges = 0, vectorEdges = 0;
                for (int y = 10; y < half - 10; y++) for (int x = 15; x < (int)(170 * scale); x++)
                {
                    int index = (y * bitmap.PixelWidth + x) * 4;
                    byte first = pixels[index], second = pixels[index + offset];
                    if (first != second) difference++;
                    if (first is > 40 and < 235) nativeEdges++;
                    if (second is > 40 and < 235) vectorEdges++;
                }
                Check(difference > 100 && vectorEdges > nativeEdges, $"{dpi} DPI title outlines replace coarse raster edges with more fractional coverage (changed={difference}, native={nativeEdges}, outline={vectorEdges})");
            }
            comparison.Close();
            UiAudit.Run(output, milliseconds => Pump(milliseconds), Check);
            report.Add("RESULT PASS; WPF component renders only; no SSH, settings writes or desktop input automation.");
            return 0;
        }
        catch (Exception error) { report.Add("FAIL " + error); Console.Error.WriteLine(error); foreach (var log in Directory.EnumerateFiles(temporary, "*.log", SearchOption.AllDirectories)) Console.Error.WriteLine(File.ReadAllText(log)); return 1; }
        finally
        {
            File.WriteAllLines(Path.Combine(output, "ui-test.log"), report);
            panel.Hide(); model.DisposeAsync().AsTask().GetAwaiter().GetResult(); logger.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            app.Shutdown();
        }
    }
    private static IEnumerable<string> GlyphFaces(Drawing? drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun.GlyphTypeface.FontUri.ToString();
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var face in GlyphFaces(child)) yield return face;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    private sealed class NoNetwork : IConnectionBackend
    {
        public Task<bool> IsLocalReadyAsync(ServerProfile s, CancellationToken ct) => throw new InvalidOperationException("UI component test must not connect.");
        public Task<HealthResult> CheckAsync(ServerProfile s, CancellationToken ct) => throw new InvalidOperationException("UI component test must not connect.");
        public Task<ITunnelSession> StartAsync(ServerProfile s, CancellationToken ct) => throw new InvalidOperationException("UI component test must not connect.");
    }
}
