using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

internal static class ExternalClickTarget
{
    internal static int Run(string marker)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new Window { Title = "Portkey isolated outside-click target", Left = 650, Top = 100, Width = 240, Height = 140,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, Topmost = true };
        var button = new Button { Content = "Outside click target" };
        button.Click += (_, _) => { File.WriteAllText(marker, "clicked"); window.Close(); };
        window.Content = button;
        window.Loaded += (_, _) => File.WriteAllText(marker + ".ready", new WindowInteropHelper(window).Handle.ToInt64().ToString());
        return app.Run(window);
    }
}
