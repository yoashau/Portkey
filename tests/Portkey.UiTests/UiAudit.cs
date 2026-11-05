using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Portkey.App;

internal static class UiAudit
{
    internal static void Run(string output, Action<int> pump, Action<bool, string> check)
    {
        var paper = Color.FromRgb(247, 244, 239);
        var ink = Color.FromRgb(38, 35, 31);
        var window = new Window { Title = "Portkey isolated UI audit", Width = 500, Height = 260, Left = 100, Top = 100,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, ShowInTaskbar = false, Background = new SolidColorBrush(paper),
            Foreground = new SolidColorBrush(ink), FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"),
            FontSize = 15, UseLayoutRounding = true, SnapsToDevicePixels = true };
        window.Resources["TextBrush"] = new SolidColorBrush(ink);
        window.Resources["MenuBrush"] = new SolidColorBrush(Color.FromRgb(255, 251, 247));
        window.Resources["SecondaryTextBrush"] = new SolidColorBrush(Color.FromRgb(112, 107, 101));
        window.Resources["ControlBrush"] = new SolidColorBrush(Color.FromArgb(184, 255, 255, 255));
        window.Resources["FieldFocusBrush"] = new SolidColorBrush(Color.FromArgb(250, 255, 255, 255));
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.Grayscale);
        var failures = new List<string>();
        void Verify(bool value, string detail)
        {
            if (value) check(true, "audit " + detail);
            else failures.Add(detail);
        }
        void Mount(UIElement child) { window.Content = new AdornerDecorator { Child = child }; pump(80); }
        TextBlock Label(string text = "代理正常 7890") => new() { Text = text, FontSize = 24, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ink), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        BitmapSource Render(FrameworkElement? root = null)
        {
            root ??= window;
            var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(root); return image;
        }
        byte[] Pixels(BitmapSource image) { var data = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(data, image.PixelWidth * 4, 0); return data; }
        void Capture(string name, FrameworkElement? root = null)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Render(root)));
            using var file = File.Create(Path.Combine(output, "audit-" + name + ".png")); encoder.Save(file);
        }
        int ClosestInk(FrameworkElement element, Color target, FrameworkElement? root = null)
        {
            root ??= window;
            var image = Render(root); var data = Pixels(image);
            var point = element.TranslatePoint(new Point(), root); int best = 255;
            for (int y = Math.Max(0, (int)Math.Floor(point.Y)); y < Math.Min(image.PixelHeight, (int)Math.Ceiling(point.Y + element.ActualHeight)); y++)
                for (int x = Math.Max(0, (int)Math.Floor(point.X)); x < Math.Min(image.PixelWidth, (int)Math.Ceiling(point.X + element.ActualWidth)); x++)
                {
                    int i = (y * image.PixelWidth + x) * 4;
                    best = Math.Min(best, Math.Max(Math.Abs(data[i] - target.B), Math.Max(Math.Abs(data[i + 1] - target.G), Math.Abs(data[i + 2] - target.R))));
                }
            return best;
        }
        Color Blend(double alpha) => Color.FromRgb((byte)Math.Round(paper.R + (ink.R - paper.R) * alpha), (byte)Math.Round(paper.G + (ink.G - paper.G) * alpha), (byte)Math.Round(paper.B + (ink.B - paper.B) * alpha));
        void Case(string name, Action action)
        {
            try { action(); Capture(name); }
            catch (Exception error) { failures.Add(name + ": " + error.Message); }
        }
        window.Show(); pump(100);
        try
        {
            Case("first-frame-hit-test", () =>
            {
                var label = Label(); bool observed = false, nonInteractive = false;
                label.Loaded += (_, _) => { observed = true; var adorners = AdornerLayer.GetAdornerLayer(label)?.GetAdorners(label); nonInteractive = adorners is null || adorners.All(x => !x.IsHitTestVisible && !x.Focusable); };
                Mount(label);
                Verify(observed && nonInteractive && AdornerLayer.GetAdornerLayer(label)?.GetAdorners(label) is { Length: > 0 } active && active.All(x => !x.IsHitTestVisible && !x.Focusable), "outline layer cannot intercept input before or after its first redraw");
                Verify(window.InputHitTest(label.TranslatePoint(new Point(12, 12), window)) is TextBlock, "hit testing reaches the native text rather than its painted outline");
            });
            Case("opacity", () =>
            {
                var label = Label(); var branch = new Border { Child = label }; var common = new Grid { Opacity = 0.5 }; common.Children.Add(branch);
                // The layer is below common, so common opacity already applies to both branches.
                window.Content = common; common.Children.Clear(); common.Children.Add(new AdornerDecorator { Child = branch }); pump(100);
                Verify(ClosestInk(label, Blend(0.5)) <= 4, "shared ancestor opacity is applied exactly once");
                branch.Opacity = 0.4; pump(100);
                Verify(ClosestInk(label, Blend(0.2)) <= 4, "nonshared ancestor opacity is preserved rather than discarded");
                common.Opacity = 1; branch.Opacity = 1; pump(100);
                Verify(ClosestInk(label, ink) <= 4, "text regains full contrast after opacity changes");
            });
            Case("live-updates", () =>
            {
                var label = Label(); Mount(label); var first = Pixels(Render());
                label.Text = "代理异常 1789"; pump(100);
                Verify(!first.SequenceEqual(Pixels(Render())), "same-length status changes repaint the actual glyphs");
                label.FontFamily = new FontFamily("Segoe UI Variable Text"); label.Text = "7890"; label.FontWeight = FontWeights.Normal; pump(100); first = Pixels(Render());
                label.FontWeight = FontWeights.Bold; pump(100);
                Verify(!first.SequenceEqual(Pixels(Render())), "variable font weight changes repaint the glyph outline");
                var brush = (SolidColorBrush)label.Foreground; brush.Color = Color.FromRgb(146, 85, 63); pump(100);
                Verify(ClosestInk(label, brush.Color) <= 4, "live brush color updates repaint at the requested contrast");
            });
            Case("clip-resize", () =>
            {
                var label = Label(); var clip = new RectangleGeometry(new Rect(0, 0, 400, 90));
                var parent = new Border { Child = label, Width = 400, Height = 90, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Clip = clip };
                Mount(parent); var first = Pixels(Render()); clip.Rect = new Rect(0, 0, 25, 90); pump(100); var image = Render(); var data = Pixels(image);
                int leaked = 0;
                for (int y = 0; y < 65; y++) for (int x = 28; x < 350; x++)
                { int i = (y * image.PixelWidth + x) * 4; if (data[i] < paper.B - 5 || data[i + 1] < paper.G - 5 || data[i + 2] < paper.R - 5) leaked++; }
                Verify(!first.SequenceEqual(data) && leaked == 0, "changing an ancestor clip without resizing the label cannot leave stale text outside it");
            });
            Case("visibility-lifecycle", () =>
            {
                var label = Label(); var holder = new Border { Child = label }; Mount(holder);
                holder.Visibility = Visibility.Hidden; pump(100);
                Verify(ClosestInk(label, paper) == 0 && !label.IsVisible, "hiding a parent hides its detached outline too");
                holder.Visibility = Visibility.Visible; pump(100);
                Verify(ClosestInk(label, ink) <= 4, "showing a parent restores visible text");
                for (int i = 0; i < 8; i++) { holder.Child = null; pump(30); Verify(label.Opacity == 1 && !OutlineTextRendering.IsActive(label), "unloading restores native text and releases its rendering session " + i); holder.Child = label; pump(30); }
                Verify(AdornerLayer.GetAdornerLayer(label)?.GetAdorners(label)?.Length == 1 && OutlineTextRendering.PaintedGlyphs(label) > 0, "repeated reattachment keeps exactly one working outline layer");
                OutlineTextRendering.SetEnabled(label, false); pump(100);
                Verify(label.Opacity == 1 && !OutlineTextRendering.IsActive(label) && AdornerLayer.GetAdornerLayer(label)?.GetAdorners(label) is null, "turning off outline rendering restores native painting without an orphan layer");
            });
            Case("button-states", () =>
            {
                var label = Label("连接"); var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(12) }; int clicks = 0; button.Click += (_, _) => clicks++;
                Mount(button); button.IsEnabled = false; pump(100);
                Verify(!button.IsEnabled && ClosestInk(label, Blend(0.4)) <= 8, "disabled controls retain their intended reduced contrast");
                button.IsEnabled = true; pump(100);
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); pump(50);
                Verify(clicks == 1 && ClosestInk(label, ink) <= 4, "reenabled buttons regain contrast and preserve native invocation");
            });
            Case("input-selection-scroll", () =>
            {
                var input = new TextBox { Text = "代理正常 abc 7890", Width = 260, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(15) };
                Mount(input); input.Focus(); input.Select(0, 4); pump(100);
                Verify(input.SelectedText == "代理正常" && OutlineTextRendering.PaintedGlyphs(input) > 0, "input selection and its painted text agree");
                var peer = (IValueProvider)new TextBoxAutomationPeer(input).GetPattern(PatternInterface.Value);
                Verify(peer.Value == input.Text, "native input accessibility exposes the original value");
                input.SelectedText = "连接设置"; input.Undo(); pump(100);
                Verify(input.Text == "代理正常 abc 7890", "native undo restores input text after outlined selection replacement");
                input.Text = string.Concat(Enumerable.Repeat("long-host-7890-", 30)); input.CaretIndex = input.Text.Length; pump(100); input.ScrollToHorizontalOffset(input.ExtentWidth); pump(100);
                Verify(input.HorizontalOffset > 0 && OutlineTextRendering.PaintedGlyphs(input) > 0, "long input scrolls horizontally while its glyphs remain painted");
                var image = Render(); var data = Pixels(image); var position = input.TranslatePoint(new Point(), window); int leaked = 0;
                for (int y = (int)position.Y; y < position.Y + input.ActualHeight; y++) for (int x = (int)(position.X + input.ActualWidth + 3); x < image.PixelWidth; x++)
                { int i = (y * image.PixelWidth + x) * 4; if (data[i] < paper.B - 5 || data[i + 1] < paper.G - 5 || data[i + 2] < paper.R - 5) leaked++; }
                Verify(leaked == 0, "horizontally scrolled input cannot paint through its right border");
            });
            Case("tooltip", () =>
            {
                var help = new Button { Style = (Style)Application.Current.FindResource("HelpMark"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(30) };
                help.IsHitTestVisible = false; // Avoid hover tint affecting the pixel measurement.
                Mount(help);
                double DeepestHelpInk()
                {
                    var image = Render(); var pixels = Pixels(image); var origin = help.TranslatePoint(new Point(), window);
                    var foreground = ((SolidColorBrush)window.Resources["SecondaryTextBrush"]).Color;
                    double deepest = 0;
                    for (int y = Math.Max(0, (int)origin.Y); y < Math.Min(image.PixelHeight, (int)Math.Ceiling(origin.Y + help.ActualHeight)); y++)
                        for (int x = Math.Max(0, (int)origin.X); x < Math.Min(image.PixelWidth, (int)Math.Ceiling(origin.X + help.ActualWidth)); x++)
                        {
                            int index = (y * image.PixelWidth + x) * 4;
                            double red = (paper.R - pixels[index + 2]) / (double)(paper.R - foreground.R);
                            double green = (paper.G - pixels[index + 1]) / (double)(paper.G - foreground.G);
                            double blue = (paper.B - pixels[index]) / (double)(paper.B - foreground.B);
                            deepest = Math.Max(deepest, Math.Min(red, Math.Min(green, blue)));
                        }
                    return deepest;
                }
                double enabledInk = DeepestHelpInk(); help.IsEnabled = false; pump(100);
                double disabledInk = DeepestHelpInk();
                Verify(enabledInk > 0.5 && disabledInk / enabledInk is > 0.3 and < 0.5, "disabled help visibly dims while preserving its normal enabled contrast");
                help.IsEnabled = true; pump(100);
                var tip = new ToolTip { Content = "服务器仅在本机端口监听。更改配置后保存。", PlacementTarget = help, Placement = PlacementMode.Bottom };
                try
                {
                    tip.IsOpen = true; pump(250);
                    var text = Descendants<TextBlock>(tip).First(x => x.IsVisible && x.Text.Length > 0);
                    Verify(OutlineTextRendering.PaintedGlyphs(text) > 0 && ClosestInk(text, ((SolidColorBrush)tip.Foreground).Color, tip) <= 4, "tooltip text uses full intended contrast and a working outline layer");
                    Capture("tooltip-popup", tip);
                }
                finally { tip.IsOpen = false; pump(50); }
            });
            Case("native-edit-menu", () =>
            {
                var input = new TextBox { Text = "代理正常 abc 7890", Width = 260, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(15) };
                Mount(input); window.Activate(); input.Focus(); input.Select(0, 4); pump(100);
                ContextMenu? OpenEditMenu()
                {
                    var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input), Environment.TickCount, Key.Apps) { RoutedEvent = Keyboard.KeyUpEvent };
                    InputManager.Current.ProcessInput(key); pump(200);
                    return PresentationSource.CurrentSources.Cast<PresentationSource>().SelectMany(x => x.RootVisual is null ? [] : Descendants<ContextMenu>(x.RootVisual)).FirstOrDefault(x => x.IsOpen);
                }
                var menu = OpenEditMenu();
                Verify(menu is not null, "native input context menu still opens from the keyboard");
                if (menu is not null)
                {
                    var copy = menu.Items.OfType<MenuItem>().FirstOrDefault(x => x.Command == ApplicationCommands.Copy);
                    Verify(copy is { IsEnabled: true }, "native Copy is enabled for selected input text");
                    Capture("native-edit-menu-popup", menu);
                    menu.IsOpen = false; pump(80); input.Select(input.Text.Length, 0); pump(80);
                    menu = OpenEditMenu();
                    Verify(menu?.Items.OfType<MenuItem>().FirstOrDefault(x => x.Command == ApplicationCommands.Copy) is { IsEnabled: false }, "native Copy is disabled only when there is no selection");
                    if (menu is not null) menu.IsOpen = false;
                }
            });
            Case("real-dpi-metadata", () =>
            {
                var label = Label(); Mount(label); var initial = VisualTreeHelper.GetDpi(window);
                try
                {
                    foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                    {
                        VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale)); pump(100);
                        Verify(Math.Abs(VisualTreeHelper.GetDpi(label).DpiScaleX - scale) < 0.001 && OutlineTextRendering.PaintedGlyphs(label) > 0, "actual visual DPI changes keep text painted at " + scale);
                        Capture("dpi-" + (int)(scale * 96));
                    }
                }
                finally { VisualTreeHelper.SetRootDpi(window, initial); pump(100); }
            });
        }
        finally { window.Close(); }
        check(failures.Count == 0, "extended UI audit: " + (failures.Count == 0 ? "all cases passed" : string.Join("; ", failures)));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var nested in Descendants<T>(VisualTreeHelper.GetChild(parent, index))) yield return nested;
    }

}
