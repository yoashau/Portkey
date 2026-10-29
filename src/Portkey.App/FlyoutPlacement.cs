using System.Drawing;

namespace Portkey.App;

internal static class FlyoutPlacement
{
    // All rectangles are physical pixels; only design distances are scaled.
    // Aulay anchors its Top flyout at (icon center, icon top - 12 DIP).
    public static Rectangle Calculate(Rectangle icon, Rectangle monitor, Rectangle work, Size size, double scale, bool centerOnIcon = true)
    {
        int gap = (int)Math.Round(12 * scale);
        int edge = (int)Math.Round(8 * scale);
        int x = centerOnIcon ? icon.Left + icon.Width / 2 - size.Width / 2 : icon.Left;
        int y = icon.Top - gap - size.Height;
        bool sideTaskbar = false;
        if (work.Top > monitor.Top && icon.Bottom <= work.Top)
            y = icon.Bottom + gap;
        else if (work.Left > monitor.Left && icon.Right <= work.Left)
        {
            sideTaskbar = true;
            x = icon.Right + gap;
            y = icon.Top + icon.Height / 2 - size.Height / 2;
        }
        else if (work.Right < monitor.Right && icon.Left >= work.Right)
        {
            sideTaskbar = true;
            x = icon.Left - gap - size.Width;
            y = icon.Top + icon.Height / 2 - size.Height / 2;
        }
        int horizontalInset = sideTaskbar ? 0 : edge;
        int verticalInset = sideTaskbar ? edge : 0;
        x = Math.Clamp(x, work.Left + horizontalInset, Math.Max(work.Left + horizontalInset, work.Right - size.Width - horizontalInset));
        y = Math.Clamp(y, work.Top + verticalInset, Math.Max(work.Top + verticalInset, work.Bottom - size.Height - verticalInset));
        return new Rectangle(x, y, size.Width, size.Height);
    }
}
