using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Microsoft.Win32;
using Portkey.Core;
using Forms = System.Windows.Forms;

namespace Portkey.App;

internal sealed class TrayManager : Forms.NativeWindow, IDisposable
{
    private const int Callback = 0x8001;
    private readonly uint taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private readonly TrayPanel panel;
    private readonly AppViewModel model;
    private readonly Func<Task> exit;
    private IntPtr icon;
    private bool disposing;
    private bool presenting;
    private ContextMenu? menu;
    private readonly MouseHook mouseCallback;
    private IntPtr mouseHook;
    private int iconPixels;
    public TrayManager(TrayPanel panel, AppViewModel model, Func<Task> exit)
    {
        this.panel = panel; this.model = model; this.exit = exit;
        mouseCallback = OnMouse;
        CreateHandle(new Forms.CreateParams { Caption = "Portkey.Tray", Style = unchecked((int)0x80000000), ExStyle = 0x80 });
        panel.Deactivated += (_, _) =>
        {
            var mouse = Forms.Cursor.Position;
            var bounds = IconBounds();
            GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundProcess);
            if (!presenting && !bounds.Contains(mouse.X, mouse.Y) && menu?.IsOpen != true && foregroundProcess != Environment.ProcessId) panel.Dismiss();
        };
        model.StatusChanged += RefreshStatus;
        panel.IsVisibleChanged += (_, _) => UpdateMouseHook();
        panel.SizeChanged += (_, _) => { if (panel.IsVisible && !presenting) PositionPanel(); };
        Refresh(add: true);
    }
    private void RefreshStatus()
    {
        if (disposing) return;
        var data = Data(icon);
        data.Flags = 4; // Only the tooltip changes; the glyph and theme are unchanged.
        data.Tip = StatusTip();
        Shell_NotifyIconW(1, ref data);
    }
    private string StatusTip()
    {
        string status = string.Join("；", model.Servers.Select(x => x.Name + " " + x.Label));
        string tip = "Portkey · " + (status.Length == 0 ? "未配置服务器" : status);
        return tip.Length > 127 ? tip[..127] : tip;
    }
    public void Refresh() { panel.ApplyTheme(force: true); Refresh(add: false); }
    private void Refresh(bool add)
    {
        if (disposing) return;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool dark = key?.GetValue("SystemUsesLightTheme") is int value && value == 0;
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        uint dpi = GetDpiForWindow(taskbar == IntPtr.Zero ? Handle : taskbar);
        int pixels = Math.Max(16, GetSystemMetricsForDpi(49, dpi)); // SM_CXSMICON, at the actual taskbar DPI.
        IntPtr next = TrayIcon.Create(pixels, dark);
        if (next == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载托盘图标。");
        var data = Data(next);
        data.Flags = 1 | 2 | 4;
        data.CallbackMessage = Callback;
        data.Tip = StatusTip();
        if (!Shell_NotifyIconW(add ? 0u : 1u, ref data))
        {
            DestroyIcon(next);
            if (add) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建系统托盘图标。");
            return;
        }
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        icon = next;
        if (add || iconPixels != pixels) model.Logger.Write("tray", $"System-font Link icon; pixels={pixels}; taskbarDpi={dpi}; no badge or bitmap rescaling");
        iconPixels = pixels;
        if (add) { data.VersionOrTimeout = 4; Shell_NotifyIconW(4, ref data); }
    }
    private NotifyIconData Data(IntPtr image) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = Handle, Id = 1, Icon = image,
        Tip = "", Info = "", InfoTitle = ""
    };
    protected override void WndProc(ref Forms.Message m)
    {
        if (disposing) { base.WndProc(ref m); return; }
        if ((uint)m.Msg == taskbarCreated) Refresh(add: true);
        else if (m.Msg is 0x007E or 0x02E0) Refresh(add: false);
        if (m.Msg == Callback)
        {
            int notification = (int)((long)m.LParam & 0xffff);
            try
            {
                if (notification is 0x400 or 0x401) { if (panel.IsVisible && !panel.IsDismissing) panel.Dismiss(); else Show(); }
                if (notification == 0x7b) ShowMenu();
            }
            catch (Exception error) { model.Logger.Write("tray", error.Message); }
        }
        if (m.Msg == 0x0010) { _ = exit(); return; }
        base.WndProc(ref m);
    }
    private Rectangle IconBounds()
    {
        var id = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = Handle, Id = 1 };
        if (Shell_NotifyIconGetRect(ref id, out var rect) == 0) return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var cursor = Forms.Cursor.Position;
        return new Rectangle(cursor.X, cursor.Y, 1, 1);
    }
    private void UpdateMouseHook()
    {
        if (panel.IsVisible && !disposing && mouseHook == IntPtr.Zero)
        {
            mouseHook = SetWindowsHookExW(14, mouseCallback, IntPtr.Zero, 0);
            if (mouseHook == IntPtr.Zero) model.Logger.Write("tray", "Outside-click hook unavailable; foreground dismissal remains active.");
        }
        else if ((!panel.IsVisible || disposing) && mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero;
        }
    }
    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && panel.IsVisible && (int)message is 0x201 or 0x204 or 0x207 or 0x20b)
        {
            var point = Marshal.PtrToStructure<MouseData>(data).Point;
            GetWindowRect(new WindowInteropHelper(panel).Handle, out var rect);
            bool inside = point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
            GetWindowThreadProcessId(WindowFromPoint(point), out uint targetProcess);
            if (!inside && targetProcess != Environment.ProcessId && !IconBounds().Contains(point.X, point.Y)) panel.Dispatcher.BeginInvoke(() => panel.Dismiss());
        }
        return CallNextHookEx(mouseHook, code, message, data);
    }
    public void Show()
    {
        if (disposing) return;
        presenting = true;
        try
        {
            var bounds = IconBounds();
            var work = Forms.Screen.FromRectangle(bounds).WorkingArea;
            var handle = PreparePanelHandle(bounds);
            double scale = GetDpiForWindow(handle) / 96.0;
            panel.Width = Math.Min(380, work.Width / scale - 16);
            panel.MaxHeight = Math.Max(160, Math.Min(panel.Height, work.Height / scale - 24));
            panel.ApplyTheme();
            // Set the final HWND bounds while hidden, before its first visible frame.
            PositionPanel();
            panel.AnimateOpen();
            panel.Show();
            panel.UpdateLayout();
            SetForegroundWindow(handle);
            panel.Activate();
            model.Logger.Write("tray", $"Panel shown; pid={Environment.ProcessId}; visible={panel.IsVisible}, active={panel.IsActive}");
        }
        finally { presenting = false; }
    }
    private IntPtr PreparePanelHandle(Rectangle bounds)
    {
        var handle = new WindowInteropHelper(panel).EnsureHandle();
        var work = Forms.Screen.FromRectangle(bounds).WorkingArea;
        // Establish the destination DPI while hidden, before either popup is measured.
        if (!panel.IsVisible) SetWindowPos(handle, IntPtr.Zero, work.Left + work.Width / 2, work.Top + work.Height / 2, 0, 0, 0x15);
        return handle;
    }
    private void PositionPanel()
    {
        var bounds = IconBounds();
        var screen = Forms.Screen.FromRectangle(bounds);
        var handle = new WindowInteropHelper(panel).Handle;
        double scale = GetDpiForWindow(handle) / 96.0;
        var size = new System.Drawing.Size((int)Math.Ceiling(panel.Width * scale), (int)Math.Ceiling(Math.Min(panel.Height, panel.MaxHeight) * scale));
        var placement = FlyoutPlacement.Calculate(bounds, screen.Bounds, screen.WorkingArea, size, scale);
        SetWindowPos(handle, new IntPtr(-1), placement.X, placement.Y, size.Width, size.Height, 0x10);
        model.Logger.Write("placement", $"icon={bounds}; panel={placement}; dpi={scale * 96:0}");
    }
    private void ShowMenu()
    {
        panel.Hide();
        var handle = PreparePanelHandle(IconBounds());
        panel.ApplyTheme();
        menu = new ContextMenu { Placement = PlacementMode.Custom, PlacementTarget = panel, Style = (Style)panel.FindResource("TrayContextMenu") };
        menu.CustomPopupPlacementCallback = (size, _, _) =>
        {
            var bounds = IconBounds();
            var screen = Forms.Screen.FromRectangle(bounds);
            double scale = GetDpiForWindow(handle) / 96.0;
            // WPF's custom-placement callback uses device pixels, including its return offset.
            var pixels = new System.Drawing.Size((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
            var placement = FlyoutPlacement.Calculate(bounds, screen.Bounds, screen.WorkingArea, pixels, scale, centerOnIcon: false);
            var origin = panel.PointToScreen(new System.Windows.Point());
            var point = new System.Windows.Point(placement.X - origin.X, placement.Y - origin.Y);
            return [new CustomPopupPlacement(point, PopupPrimaryAxis.None)];
        };
        void Add(string title, Func<Task> action)
        {
            var item = new MenuItem { Header = title };
            item.Click += async (_, _) => { try { await action(); } catch (Exception error) { model.Message = error.Message; model.Logger.Write("menu", error.Message); } };
            menu.Items.Add(item);
        }
        Add("显示 Portkey", () => { Show(); return Task.CompletedTask; });
        Add("连接 / 暂停", () => model.ToggleAsync());
        Add("重新连接", async () => { if (model.Selected is not null) await model.Selected.Manager.ReconnectAsync(); });
        Add("查看日志", () => { panel.ShowLogs(); Show(); return Task.CompletedTask; });
        menu.Items.Add(new Separator());
        Add("退出", exit);
        SetForegroundWindow(Handle);
        menu.IsOpen = true;
    }
    public void Dispose()
    {
        disposing = true;
        UpdateMouseHook();
        model.StatusChanged -= RefreshStatus;
        menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var data = Data(icon); Shell_NotifyIconW(2, ref data);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        DestroyHandle();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MousePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public MousePoint Point; public uint Mouse, Flags, Time; public UIntPtr Extra; }
    private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int type, MouseHook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier id, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string text);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string className, string? windowName);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(MousePoint point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
}

internal static class NativeWindowStyle
{
    public static bool Apply(IntPtr window, bool dark, bool transparency)
    {
        if (window == IntPtr.Zero) return false;
        int value = dark ? 1 : 0; DwmSetWindowAttribute(window, 20, ref value, 4);
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            value = 2; DwmSetWindowAttribute(window, 33, ref value, 4);
        }
        bool acrylic = false;
        if (Environment.OSVersion.Version.Build >= 22621)
        {
            value = transparency ? 3 : 1; // DWMSBT_TRANSIENTWINDOW / DWMSBT_NONE.
            acrylic = DwmSetWindowAttribute(window, 38, ref value, 4) == 0 && transparency;
        }
        if (HwndSource.FromHwnd(window)?.CompositionTarget is { } target) target.BackgroundColor = System.Windows.Media.Colors.Transparent;
        return acrylic;
    }
    public static void SetSlideClip(IntPtr window, double offset, double width, double height, double scale)
    {
        if (offset <= 0) { SetWindowRgn(window, IntPtr.Zero, true); return; }
        int y = (int)Math.Ceiling(offset * scale);
        int w = (int)Math.Ceiling(width * scale), h = (int)Math.Ceiling(height * scale);
        int radius = (int)Math.Round(16 * scale);
        var region = CreateRoundRectRgn(0, y, w + 1, h + y + 1, radius, radius);
        // Windows owns the region after success; on failure it is still ours.
        if (region != IntPtr.Zero && SetWindowRgn(window, region, true) == 0) DeleteObject(region);
    }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
