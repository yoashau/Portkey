using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Imaging = System.Drawing.Imaging;

namespace Portkey.App;

internal static class TrayIcon
{
    internal const char Link = '\uE71B';
    private static readonly Typeface Typeface = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    // The system icon font supplies the same 16-epx monoline construction used
    // by Windows symbols. Render each physical size directly, with no logo padding.
    internal static BitmapSource RenderImage(char glyph, int pixels, Color color)
    {
        var visual = new DrawingVisual();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        var text = new FormattedText(glyph.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, pixels, new SolidColorBrush(color), 1);
        var outline = text.BuildGeometry(new Point());
        // Keep Portkey's diagonal link silhouette, using the system glyph's stroke.
        if (glyph == Link) outline.Transform = new RotateTransform(-45);
        var ink = outline.Bounds;
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new TranslateTransform(Math.Round((pixels - ink.Width) / 2 - ink.X), Math.Round((pixels - ink.Height) / 2 - ink.Y)));
            if (glyph == Link) drawing.PushTransform(new RotateTransform(-45));
            drawing.DrawText(text, new Point());
        }
        var image = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze();
        return image;
    }

    internal static IntPtr Create(int pixels, bool light)
    {
        var color = SystemParameters.HighContrast ? SystemColors.WindowTextColor : light ? Colors.White : Color.FromRgb(32, 32, 32);
        var image = RenderImage(Link, pixels, color);
        int stride = pixels * 4;
        var buffer = new byte[stride * pixels];
        image.CopyPixels(buffer, stride, 0);
        using var bitmap = new Drawing.Bitmap(pixels, pixels, Imaging.PixelFormat.Format32bppPArgb);
        var locked = bitmap.LockBits(new Drawing.Rectangle(0, 0, pixels, pixels), Imaging.ImageLockMode.WriteOnly, Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            for (int row = 0; row < pixels; row++)
                Marshal.Copy(buffer, row * stride, IntPtr.Add(locked.Scan0, row * locked.Stride), stride);
        }
        finally { bitmap.UnlockBits(locked); }
        return bitmap.GetHicon(); // Caller owns and destroys only this returned handle.
    }
}
