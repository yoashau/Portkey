using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Portkey.App;
using Portkey.Core;
using Portkey.Infrastructure;

internal static class InteractionTests
{
    internal static void Run(string output, string temporary, TrayPanel panel, AppViewModel model,
        TaskCompletionSource<ResolvedHost> slow, TaskCompletionSource<ResolvedHost> slowEdit,
        bool settingsOnly, Action<int> pump, Action<bool, string> check)
    {
        T Named<T>(string name) where T : FrameworkElement => (T)panel.FindName(name);
        Button ButtonNamed(string name) => Descendants<Button>(panel).Single(x => AutomationProperties.GetName(x) == name);
        void ClickAt(FrameworkElement element)
        {
            if (!panel.IsActive)
            {
                SetForegroundWindow(new WindowInteropHelper(panel).Handle);
                panel.Activate(); pump(100);
            }
            var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            SetCursorPos((int)point.X, (int)point.Y); pump(50);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); pump(30);
            mouse_event(4, 0, 0, 0, UIntPtr.Zero); pump(120);
        }
        void OpenEditor() { ButtonNamed("连接设置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump(350); }
        void Back() { Named<Button>("BackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump(180); }
        ServerProfile ReadEditor() => (ServerProfile)typeof(TrayPanel).GetMethod("ReadEditor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(panel, null)!;
        byte[] Capture(string name)
        {
            var bitmap = new RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
            return pixels;
        }
        int TextDifference(byte[] first, byte[] second, TextBox logs)
        {
            var origin = logs.TranslatePoint(new Point(14, 20), panel);
            int width = (int)panel.ActualWidth, different = 0;
            for (int y = (int)origin.Y; y < origin.Y + 140; y++)
                for (int x = (int)origin.X; x < origin.X + 100; x++)
                {
                    int index = (y * width + x) * 4;
                    if (Math.Abs(first[index] - second[index]) > 5 || Math.Abs(first[index + 1] - second[index + 1]) > 5 || Math.Abs(first[index + 2] - second[index + 2]) > 5) different++;
                }
            return different;
        }

        POINT originalMouse = default;
        if (!settingsOnly) GetCursorPos(out originalMouse);
        try
        {
            // The settings file and SSH config are isolated fixtures; no real
            // connection, user profile, or remote configuration is touched.
            var saved = new ServerProfile { Name = "saved", Host = "second", User = "saved-user", SshPort = 2223, IdentityFile = "C:/fixture/saved-key" };
            var store = new SettingsStore(Path.Combine(temporary, "roundtrip"));
            Task.Run(() => store.SaveAsync(new AppSettings { Servers = [saved] })).GetAwaiter().GetResult();
            var loaded = Task.Run(() => store.LoadAsync()).GetAwaiter().GetResult().Servers.Single();
            var original = model.Selected!;
            var second = new ConnectionViewModel(loaded, new NoNetwork(), model.Logger, () => { });
            model.Servers.Add(second);

            panel.AnimateOpen(); panel.Show(); panel.Activate(); pump(350);
            var selector = Named<ComboBox>("ServerSelector");
            if (!settingsOnly)
            {
            var toggle = (ToggleButton)selector.Template.FindName("DropDownToggle", selector);
            ClickAt(selector);
            check(selector.IsDropDownOpen, "mouse opens the main server selector");
            ClickAt(Named<Grid>("ConnectionCard"));
            check(!selector.IsDropDownOpen && Equals(toggle.Background, selector.Background), "one outside click closes the selector and removes its persistent filled highlight");
            SetForegroundWindow(new WindowInteropHelper(panel).Handle); panel.Activate(); selector.Focus(); pump(80);
            keybd_event(9, 0, 0, UIntPtr.Zero); pump(40);
            keybd_event(9, 0, 2, UIntPtr.Zero); pump(120);
            var focused = Keyboard.FocusedElement as UIElement;
            var focusLayer = focused is null ? null : AdornerLayer.GetAdornerLayer(focused);
            var focusAdorners = focused is null ? null : focusLayer?.GetAdorners(focused);
            check(focused is not null && focusAdorners?.Any(x => x.GetType().Name.Contains("FocusVisual", StringComparison.Ordinal)) == true,
                "keyboard navigation still displays a visible focus outline");
            ClickAt(selector);
            check(selector.IsDropDownOpen, "server popup reopens after keyboard navigation");
            var secondItem = (ComboBoxItem)selector.ItemContainerGenerator.ContainerFromItem(second);
            ClickAt(secondItem);
            check(ReferenceEquals(model.Selected, second) && !selector.IsDropDownOpen, "actual mouse selection in the popup still selects the intended server");
            model.Selected = original; pump(100);
            using (var tray = new TrayManager(panel, model, () => Task.CompletedTask))
            {
                string marker = Path.Combine(temporary, "outside-click");
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add("--outside-target"); start.ArgumentList.Add(marker);
                using var target = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated click target.");
                try
                {
                    for (int i = 0; i < 100 && !File.Exists(marker + ".ready"); i++) pump(40);
                    check(File.Exists(marker + ".ready"), "isolated external window is ready for an outside click");
                    var handle = new IntPtr(long.Parse(File.ReadAllText(marker + ".ready")));
                    check(GetWindowRect(handle, out var bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top,
                        "isolated external window has valid screen bounds");
                    tray.Show(); pump(350); ClickAt(selector);
                    check(selector.IsDropDownOpen, "selector is open before the external click");
                    SetCursorPos((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2); pump(60);
                    mouse_event(2, 0, 0, 0, UIntPtr.Zero); pump(60);
                    mouse_event(4, 0, 0, 0, UIntPtr.Zero); pump(300);
                    check(File.Exists(marker), "the first click outside the panel reaches another application");
                }
                finally { try { if (!target.HasExited) target.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
                tray.Show(); pump(350); ClickAt(selector);
                tray.OnOutsideMouseDown(onTrayIcon: true); pump(80);
                check(!selector.IsDropDownOpen && Mouse.Captured != selector && panel.IsVisible,
                    "a tray icon click releases dropdown capture and lets the tray callback control dismissal");
            }
            panel.AnimateOpen(); panel.Show(); panel.Activate(); pump(350);
            ClickAt(selector); ClickAt(ButtonNamed("连接日志"));
            check(Named<Grid>("LogsPage").IsVisible && !selector.IsDropDownOpen, "the first click on another action both closes the dropdown and activates that action");
            }
            else { panel.ShowLogs(); pump(180); }

            var logs = Named<TextBox>("LogsText");
            logs.Text = string.Join('\n', Enumerable.Range(0, 50).Select(i => $"{i:000} LEFT " + new string('W', 180) + " MIDDLE " + new string('i', 180) + " RIGHT"));
            pump(300);
            var scroll = (ScrollViewer)logs.Template.FindName("PART_ContentHost", logs);
            var horizontal = Descendants<ScrollBar>(scroll).Single(x => x.Orientation == Orientation.Horizontal);
            var track = (Track)horizontal.Template.FindName("PART_Track", horizontal);
            check(horizontal.IsVisible && scroll.ScrollableWidth > 100, "long log lines expose a usable horizontal scrollbar");
            var before = Capture("interaction-logs-before.png");
            if (settingsOnly)
            {
                track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(100, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                track.Thumb.RaiseEvent(new DragCompletedEventArgs(100, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                pump(200);
            }
            else
            {
                var point = track.Thumb.PointToScreen(new Point(track.Thumb.ActualWidth / 2, track.Thumb.ActualHeight / 2));
                SetCursorPos((int)point.X, (int)point.Y); pump(60);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); pump(60);
                SetCursorPos((int)point.X + 100, (int)point.Y); pump(100);
                mouse_event(4, 0, 0, 0, UIntPtr.Zero); pump(200);
            }
            var after = Capture("interaction-logs-after.png");
            int changed = TextDifference(before, after, logs);
            check(scroll.HorizontalOffset > 100 && changed > 300, $"{(settingsOnly ? "simulated" : "actual")} scrollbar drag moves both offset and rendered log text (changed pixels={changed})");
            logs.ScrollToHorizontalOffset(0); pump(200);
            var returned = Capture("interaction-logs-returned.png");
            check(scroll.HorizontalOffset < 1 && TextDifference(before, returned, logs) < 40, "horizontal scrolling back restores the original log text");
            logs.ScrollToVerticalOffset(scroll.ScrollableHeight); pump(200);
            check(scroll.VerticalOffset > 0 && TextDifference(returned, Capture("interaction-logs-vertical.png"), logs) > 100,
                "vertical log scrolling also updates the rendered line positions");
            Back();

            OpenEditor();
            var aliases = Named<ComboBox>("HostAliases");
            var user = Named<TextBox>("UserInput");
            var port = Named<TextBox>("SshPortInput");
            var identity = Named<TextBox>("IdentityInput");
            check(Equals(aliases.SelectedItem, "example") && user.Text == "fixture-user" && port.Text == "2201" && identity.Text == "C:/fixture/example-key",
                "reopening the current server restores its matching SSH alias and inherited values");
            var inherited = ReadEditor();
            check(inherited.User == "" && inherited.SshPort is null && inherited.IdentityFile == "", "displaying inherited SSH values does not save them as connection overrides");
            user.SelectAll(); user.SelectedText = "temporary-user"; user.Undo(); pump(80);
            check(user.Text == "fixture-user" && ReadEditor().User == "",
                "undoing an edit back to the inherited value does not create a saved override");
            user.Text = "custom-user";
            var edited = ReadEditor();
            check(edited.User == "custom-user" && edited.SshPort is null && edited.IdentityFile == "", "editing one inherited value makes only that field an override");

            if (!settingsOnly)
            {
                ClickAt(aliases); ClickAt(Named<Button>("BackButton"));
                check(!aliases.IsDropDownOpen && Named<Grid>("HomePage").IsVisible, "SSH alias dropdown gives the first outside click to the back action");
                OpenEditor();
            }
            aliases.SelectedItem = "second"; pump(300);
            check(user.Text == "imported-user" && port.Text == "2222" && identity.Text == "C:/fixture/second-key", "importing a different SSH alias loads that alias's explicit settings");
            aliases.SelectedItem = "precedence-one"; pump(250);
            check(user.Text == "first-user" && port.Text == "2255", "import uses OpenSSH's effective precedence when config patterns overlap");
            Back(); model.Selected = second; OpenEditor();
            check(Equals(aliases.SelectedItem, "second") && user.Text == "saved-user" && port.Text == "2223" && identity.Text == "C:/fixture/saved-key",
                "settings round-tripped through storage display the selected server's saved overrides");
            check(Named<Button>("DeleteButton").IsEnabled, "an existing selected connection can still be deleted");
            Back(); OpenEditor();
            check(Equals(aliases.SelectedItem, "second") && user.Text == "saved-user" && port.Text == "2223" && identity.Text == "C:/fixture/saved-key",
                "saved server details and alias survive repeated visits without reimporting");

            aliases.SelectedItem = "slow"; pump(60);
            check(!Named<Button>("SaveButton").IsEnabled && Equals(Named<Button>("SaveButton").Content, "正在读取 SSH 配置"),
                "saving is unavailable and explains why while an SSH import is still loading");
            ButtonNamed("新建连接").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump(80);
            Named<Button>("DeleteButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(!Named<Button>("DeleteButton").IsEnabled && ReferenceEquals(model.Selected, second) && model.Message.Length == 0,
                "an unsaved new connection cannot delete the previously selected server");
            slow.SetResult(new ResolvedHost("slow", "slow-user", "2299", "none", false)); pump(250);
            check(Named<TextBox>("HostInput").Text == "" && user.Text == "" && port.Text == "" && identity.Text == "" && aliases.SelectedItem is null && Named<Button>("SaveButton").IsEnabled,
                "a late SSH lookup cannot populate a newly created server or leave saving disabled");
            aliases.SelectedItem = "slowedit"; pump(60);
            port.Focus(); Named<TextBox>("NameInput").Focus();
            user.Text = "typed-while-loading";
            slowEdit.SetResult(new ResolvedHost("slowedit", "slowedit-user", "2288", "none", false)); pump(250);
            check(user.Text == "typed-while-loading" && port.Text == "3399" && ReadEditor().SshPort == 3399 && Named<Button>("SaveButton").IsEnabled,
                "a late import re-resolves dependent fields without overwriting edits made during lookup");
            aliases.SelectedItem = "bare"; pump(250);
            check(user.Text == "default-user" && port.Text == "22" && identity.Text == "", "SSH defaults are displayed while an unspecified key remains empty");
            var bare = ReadEditor();
            check(bare.User == "" && bare.SshPort is null && bare.IdentityFile == "", "default SSH values remain inherited when saved");
            aliases.SelectedItem = "unavailable"; pump(250);
            check(user.Text == "" && model.Message.Contains("未能完整读取", StringComparison.Ordinal),
                "a failed SSH lookup warns instead of importing unverified values");
            model.ClearMessage();
            aliases.SelectedItem = "timeout"; pump(250);
            check(user.Text == "" && Named<Button>("SaveButton").IsEnabled && model.Message.Contains("未能完整读取", StringComparison.Ordinal),
                "a timed-out SSH lookup reports the failure without importing unverified values and re-enables saving");
            model.ClearMessage();
            Back();
            var conditional = new ConnectionViewModel(new ServerProfile { Name = "conditional", Host = "conditional" }, new NoNetwork(), model.Logger, () => { });
            model.Servers.Add(conditional); model.Selected = conditional; OpenEditor();
            check(user.Text == "default-user" && port.Text == "22" && identity.Text == "", "the editor displays the initially resolved settings for a conditional SSH host");
            user.Text = "alternate";
            if (settingsOnly)
                user.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, user, Named<TextBox>("NameInput")) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            else { user.Focus(); Named<TextBox>("NameInput").Focus(); }
            pump(300);
            var conditionalProfile = ReadEditor();
            check(user.Text == "alternate" && port.Text == "2311" && identity.Text == "C:/fixture/conditional-key"
                && conditionalProfile.SshPort is null && conditionalProfile.IdentityFile == "",
                "changing an SSH user refreshes conditional port and identity without saving inherited values as overrides");

            // Moving a common ancestor must not dispatch a redraw for every
            // outline label on every animation frame.
            var surface = (TranslateTransform)Named<Border>("Surface").RenderTransform;
            var page = (TranslateTransform)panel.FindName("PageTranslation");
            surface.BeginAnimation(TranslateTransform.YProperty, null);
            page.BeginAnimation(TranslateTransform.XProperty, null); pump(150);
            int queued = 0;
            void Posted(object? sender, DispatcherHookEventArgs e) { if (e.Operation.Priority == DispatcherPriority.Loaded) queued++; }
            panel.Dispatcher.Hooks.OperationPosted += Posted;
            var watch = Stopwatch.StartNew();
            try { for (int i = 0; i < 20; i++) { surface.Y = page.X = i % 2 == 0 ? 2 : 0; pump(15); } }
            finally { panel.Dispatcher.Hooks.OperationPosted -= Posted; surface.Y = page.X = 0; }
            check(queued < 40, $"20 panel and page animation steps avoid per-label redraw dispatches (queued={queued}, elapsed={watch.Elapsed.TotalMilliseconds:F0} ms)");

            if (!settingsOnly)
            {
                var protocol = Named<ComboBox>("ProtocolInput");
                protocol.BringIntoView(); pump(200); ClickAt(protocol); ClickAt(Named<Button>("BackButton"));
                check(Named<Grid>("HomePage").IsVisible && !protocol.IsDropDownOpen, "protocol dropdown also forwards the first click to the back action");
            }
        }
        finally { if (!settingsOnly) { mouse_event(4, 0, 0, 0, UIntPtr.Zero); SetCursorPos(originalMouse.X, originalMouse.Y); } }
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
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    private sealed class NoNetwork : IConnectionBackend
    {
        public Task<bool> IsLocalReadyAsync(ServerProfile s, CancellationToken ct) => throw new InvalidOperationException("UI fixture cannot connect.");
        public Task<HealthResult> CheckAsync(ServerProfile s, CancellationToken ct) => throw new InvalidOperationException("UI fixture cannot connect.");
        public Task<ITunnelSession> StartAsync(ServerProfile s, CancellationToken ct) => throw new InvalidOperationException("UI fixture cannot connect.");
    }
}
