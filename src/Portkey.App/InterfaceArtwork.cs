using System.Windows;
using System.Windows.Media;

namespace Portkey.App;

public static class InterfaceArtwork
{
    // Preserve the existing system link silhouette but rasterize its curves as
    // geometry. Font hinting at small sizes must not dictate the logo's edges.
    public static Geometry Link { get; } = CreateLink();

    private static Geometry CreateLink()
    {
        var face = new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        if (!face.TryGetGlyphTypeface(out var glyphs) || !glyphs.CharacterToGlyphMap.TryGetValue(TrayIcon.Link, out var index))
            throw new InvalidOperationException("The system link glyph is unavailable.");
        var geometry = glyphs.GetGlyphOutline(index, 26, 26);
        geometry.Freeze();
        return geometry;
    }
}
