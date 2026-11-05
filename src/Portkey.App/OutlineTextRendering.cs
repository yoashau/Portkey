using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Portkey.App;

/// <summary>
/// Keeps WPF's text layout, input, selection and accessibility, but paints glyph
/// outlines with the geometry rasterizer. In particular, diagonal CJK strokes
/// must have coverage antialiasing in both axes, rather than the font rasterizer's
/// small-size grid fitting. No bitmap scaling or additional fonts are involved.
/// </summary>
public static class OutlineTextRendering
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(OutlineTextRendering), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty SessionProperty = DependencyProperty.RegisterAttached(
        "Session", typeof(Session), typeof(OutlineTextRendering));
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    internal static bool IsActive(DependencyObject element) => element.GetValue(SessionProperty) is Session { Active: true };
    internal static int PaintedGlyphs(DependencyObject element) => (element.GetValue(SessionProperty) as Session)?.PaintedGlyphs ?? 0;

    private static void EnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement owner) return;
        if ((bool)args.NewValue)
        {
            owner.Loaded += Start;
            owner.Unloaded += Stop;
            if (owner.IsLoaded) Start(owner, new RoutedEventArgs());
        }
        else
        {
            owner.Loaded -= Start;
            owner.Unloaded -= Stop;
            Stop(owner, new RoutedEventArgs());
        }
    }
    private static void Start(object sender, RoutedEventArgs args)
    {
        var owner = (FrameworkElement)sender;
        if (owner.GetValue(SessionProperty) is not null) return;
        var session = new Session(owner);
        owner.SetValue(SessionProperty, session);
        session.Start();
    }
    private static void Stop(object sender, RoutedEventArgs args)
    {
        var owner = (FrameworkElement)sender;
        (owner.GetValue(SessionProperty) as Session)?.Dispose();
        owner.ClearValue(SessionProperty);
    }

    private sealed class Session(FrameworkElement owner) : IDisposable
    {
        private FrameworkElement? source;
        private AdornerLayer? layer;
        private OutlineAdorner? adorner;
        private double originalOpacity;
        private bool queued;
        private bool disposed;
        private bool waitingForLayer;
        private readonly List<(DependencyObject Target, DependencyPropertyDescriptor Property, EventHandler Handler)> watchers = [];
        private readonly List<Freezable> contextValues = [];
        public bool Active => adorner is not null;
        public int PaintedGlyphs => adorner?.GlyphCount ?? 0;

        public void Start()
        {
            if (disposed || adorner is not null) return;
            owner.ApplyTemplate();
            source = owner is TextBox ? FindTextView(owner) : owner;
            layer = source is null ? null : AdornerLayer.GetAdornerLayer(source);
            if (source is null || layer is null)
            {
                // A disconnected template may have no layer yet. Keep native
                // text visible and retry on layout, never spin the dispatcher.
                if (!waitingForLayer) { owner.LayoutUpdated += RetryOnLayout; waitingForLayer = true; }
                return;
            }
            owner.LayoutUpdated -= RetryOnLayout;
            waitingForLayer = false;
            originalOpacity = source.Opacity;
            adorner = new OutlineAdorner(source) { IsHitTestVisible = false, Focusable = false };
            layer.Add(adorner);
            // Opacity does not change layout, the retained native drawing or UIA.
            // The mirrored drawing includes selection/IME marks, not just glyphs.
            source.SetCurrentValue(UIElement.OpacityProperty, 0.0);
            for (DependencyObject? item = source; item is UIElement element; item = VisualTreeHelper.GetParent(item))
            {
                Watch(element, UIElement.ClipProperty, ContextChanged);
                Watch(element, UIElement.ClipToBoundsProperty, ContextChanged);
                Watch(element, UIElement.RenderTransformProperty, ContextChanged);
                if (!ReferenceEquals(item, source)) Watch(element, UIElement.OpacityProperty, ContextChanged);
                if (element is FrameworkElement framework) Watch(framework, FrameworkElement.LayoutTransformProperty, ContextChanged);
            }
            RefreshContextValues();
            owner.LayoutUpdated += LayoutUpdated;
            owner.IsVisibleChanged += Changed;
            owner.IsEnabledChanged += Changed;
            owner.SizeChanged += SizeChanged;
            foreach (var property in new[] { Control.FontFamilyProperty, Control.FontSizeProperty,
                Control.FontWeightProperty, Control.FontStyleProperty, Control.FontStretchProperty,
                Control.ForegroundProperty, FrameworkElement.FlowDirectionProperty }) Watch(property);
            if (owner is TextBlock) Watch(TextBlock.TextProperty);
            if (owner is TextBox box)
            {
                box.TextChanged += TextChanged;
                box.SelectionChanged += SelectionChanged;
                box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
                box.GotKeyboardFocus += FocusChanged;
                box.LostKeyboardFocus += FocusChanged;
            }
            Queue();
        }
        private static FrameworkElement? FindTextView(DependencyObject parent)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is FrameworkElement view && child.GetType().Name == "TextBoxView") return view;
                var found = FindTextView(child);
                if (found is not null) return found;
            }
            return null;
        }
        private void Watch(DependencyProperty property) => Watch(owner, property, Queue);
        private void Watch(DependencyObject target, DependencyProperty property, Action changed)
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(property, target.GetType());
            if (descriptor is null) return;
            EventHandler handler = (_, _) => changed();
            descriptor.AddValueChanged(target, handler);
            watchers.Add((target, descriptor, handler));
        }
        private void ContextChanged()
        {
            RefreshContextValues();
            adorner?.InvalidateVisual();
            Queue();
        }
        private void ContextValueChanged(object? sender, EventArgs args)
        {
            adorner?.InvalidateVisual();
            Queue();
        }
        private void RefreshContextValues()
        {
            foreach (var value in contextValues) value.Changed -= ContextValueChanged;
            contextValues.Clear();
            for (DependencyObject? item = source; item is UIElement element; item = VisualTreeHelper.GetParent(item))
            {
                Track(element.Clip); Track(element.RenderTransform);
                if (element is FrameworkElement framework) Track(framework.LayoutTransform);
            }
            void Track(Freezable? value)
            {
                if (value is null || value.IsFrozen || contextValues.Contains(value)) return;
                contextValues.Add(value); value.Changed += ContextValueChanged;
            }
        }
        private void RetryOnLayout(object? sender, EventArgs args) => Queue();
        private void LayoutUpdated(object? sender, EventArgs args) => Queue();
        private void Changed(object sender, DependencyPropertyChangedEventArgs args) => Queue();
        private void SizeChanged(object sender, SizeChangedEventArgs args) => Queue();
        private void TextChanged(object sender, TextChangedEventArgs args) => Queue();
        private void SelectionChanged(object sender, RoutedEventArgs args) => Queue();
        private void Scrolled(object sender, ScrollChangedEventArgs args) => Queue();
        private void FocusChanged(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs args) => Queue();
        private void Queue()
        {
            if (disposed || queued || owner.Dispatcher.HasShutdownStarted) return;
            queued = true;
            // Run after native layout/OnRender has built the current GlyphRuns.
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                queued = false;
                if (disposed || !owner.IsLoaded) return;
                if (adorner is null) { Start(); return; }
                if (source is null) return;
                // The adorner already inherits opacity from ancestors shared
                // with its source (notably ContextMenu's opening animation).
                // Mirror only the branch between the source and that ancestor.
                var shared = new HashSet<DependencyObject>();
                for (DependencyObject? item = layer; item is not null; item = VisualTreeHelper.GetParent(item))
                    shared.Add(item);
                double opacity = originalOpacity;
                for (DependencyObject? item = VisualTreeHelper.GetParent(source); item is not null && !shared.Contains(item); item = VisualTreeHelper.GetParent(item))
                    if (item is UIElement visual) opacity *= visual.Opacity;
                adorner.Opacity = opacity;
                adorner.Visibility = source.IsVisible ? Visibility.Visible : Visibility.Collapsed;
                adorner.Refresh();
            });
        }
        public void Dispose()
        {
            disposed = true;
            owner.LayoutUpdated -= LayoutUpdated;
            owner.LayoutUpdated -= RetryOnLayout;
            owner.IsVisibleChanged -= Changed;
            owner.IsEnabledChanged -= Changed;
            owner.SizeChanged -= SizeChanged;
            foreach (var (target, property, handler) in watchers) property.RemoveValueChanged(target, handler);
            watchers.Clear();
            foreach (var value in contextValues) value.Changed -= ContextValueChanged;
            contextValues.Clear();
            if (owner is TextBox box)
            {
                box.TextChanged -= TextChanged;
                box.SelectionChanged -= SelectionChanged;
                box.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
                box.GotKeyboardFocus -= FocusChanged;
                box.LostKeyboardFocus -= FocusChanged;
            }
            if (adorner is not null) layer?.Remove(adorner);
            if (source is not null && adorner is not null) source.SetCurrentValue(UIElement.OpacityProperty, originalOpacity);
            adorner = null;
        }
    }

    private sealed class OutlineAdorner(FrameworkElement source) : Adorner(source)
    {
        private Drawing? drawing;
        private string paintSignature = "";
        private string layoutSignature = "";
        public int GlyphCount { get; private set; }
        public void Refresh()
        {
            // GetDrawing produces a new wrapper; a stable signature prevents a
            // new geometry on every unrelated layout pass or caret blink.
            var native = Snapshot(source);
            var layer = AdornerLayer.GetAdornerLayer(source);
            var origin = layer is null ? new Point() : source.TranslatePoint(new Point(), layer);
            string paint = Fingerprint(native);
            string layout = origin + "|" + source.RenderSize + "|" + ClipFingerprint();
            if (paintSignature == paint && layoutSignature == layout && drawing is not null) return;
            // Scrolling or clipping changes need a repaint, not new font outlines.
            if (paintSignature != paint || drawing is null)
            {
                paintSignature = paint;
                GlyphCount = CountGlyphs(native);
                drawing = Outline(native);
            }
            layoutSignature = layout;
            InvalidateVisual();
        }
        private static DrawingGroup Snapshot(Visual visual)
        {
            var result = new DrawingGroup();
            if (VisualTreeHelper.GetDrawing(visual) is { } own) result.Children.Add(own);
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(visual); index++)
            {
                if (VisualTreeHelper.GetChild(visual, index) is not Visual child) continue;
                var branch = Snapshot(child);
                var transform = new TransformGroup();
                if (VisualTreeHelper.GetTransform(child) is { } local) transform.Children.Add(local);
                var offset = VisualTreeHelper.GetOffset(child);
                transform.Children.Add(new TranslateTransform(offset.X, offset.Y));
                branch.Transform = transform;
                branch.Opacity = VisualTreeHelper.GetOpacity(child);
                branch.ClipGeometry = VisualTreeHelper.GetClip(child);
                result.Children.Add(branch);
            }
            return result;
        }
        private static string Fingerprint(Drawing? native)
        {
            if (native is null) return "";
            if (native is GlyphRunDrawing glyph)
                return $"{glyph.ForegroundBrush}|{glyph.GlyphRun.FontRenderingEmSize}|{glyph.GlyphRun.BaselineOrigin}|{string.Join(',', glyph.GlyphRun.GlyphIndices)}|{string.Join(',', glyph.GlyphRun.AdvanceWidths)}|{string.Join(',', glyph.GlyphRun.GlyphOffsets ?? [])}|{glyph.GlyphRun.GlyphTypeface.FontUri}|{glyph.GlyphRun.GlyphTypeface.Weight}|{glyph.GlyphRun.GlyphTypeface.Style}|{glyph.GlyphRun.GlyphTypeface.Stretch}|{glyph.GlyphRun.GlyphTypeface.StyleSimulations}|{glyph.GlyphRun.IsSideways}|{glyph.GlyphRun.BidiLevel}|{glyph.GlyphRun.PixelsPerDip}";
            if (native is DrawingGroup group)
                return $"{group.Transform}|{group.ClipGeometry}|{group.Opacity}|" + string.Join(';', group.Children.Select(Fingerprint));
            return native is GeometryDrawing shape ? $"{shape.Brush}|{shape.Pen}|{shape.Geometry}" : native.ToString() ?? "";
        }
        private static int CountGlyphs(Drawing native) => native switch
        {
            GlyphRunDrawing run => run.GlyphRun.GlyphIndices.Count,
            DrawingGroup group => group.Children.Sum(CountGlyphs),
            _ => 0
        };
        private static Drawing? Outline(Drawing? native)
        {
            if (native is GlyphRunDrawing glyph)
            {
                var geometry = glyph.GlyphRun.BuildGeometry();
                geometry.Freeze();
                return new GeometryDrawing(glyph.ForegroundBrush, null, geometry);
            }
            if (native is DrawingGroup group)
            {
                var result = new DrawingGroup { Transform = group.Transform, ClipGeometry = group.ClipGeometry, Opacity = group.Opacity, OpacityMask = group.OpacityMask };
                foreach (var child in group.Children)
                    if (Outline(child) is { } copy) result.Children.Add(copy);
                return result;
            }
            return native;
        }
        private string ClipFingerprint()
        {
            var result = new System.Text.StringBuilder();
            for (DependencyObject? item = source; item is Visual visual; item = VisualTreeHelper.GetParent(item))
                if (visual is UIElement element && (element.Clip is not null || element.ClipToBounds))
                    result.Append(element.Clip).Append('|').Append(element.ClipToBounds ? element.RenderSize : Size.Empty)
                        .Append('|').Append(RelativeMatrix(visual.TransformToVisual(source))).Append(';');
            return result.ToString();
        }
        private static Matrix RelativeMatrix(GeneralTransform transform)
        {
            var origin = transform.Transform(new Point());
            var horizontal = transform.Transform(new Point(1, 0));
            var vertical = transform.Transform(new Point(0, 1));
            return new Matrix(horizontal.X - origin.X, horizontal.Y - origin.Y,
                vertical.X - origin.X, vertical.Y - origin.Y, origin.X, origin.Y);
        }
        protected override void OnRender(DrawingContext context)
        {
            if (drawing is null) return;
            int clips = 0;
            // Adorners live outside the scroller. Reapply every ancestor clip,
            // including the rounded panel, so glyphs cannot cover fixed chrome.
            for (DependencyObject? item = source; item is Visual visual; item = VisualTreeHelper.GetParent(item))
            {
                if (visual is not UIElement element) continue;
                var transform = visual.TransformToVisual(source);
                if (element.Clip is { } clip) PushClip(clip);
                if (element.ClipToBounds) PushClip(new RectangleGeometry(new Rect(element.RenderSize)));
                void PushClip(Geometry original)
                {
                    var copy = original.CloneCurrentValue();
                    var combined = new TransformGroup();
                    if (copy.Transform is { } local) combined.Children.Add(local);
                    combined.Children.Add(new MatrixTransform(RelativeMatrix(transform)));
                    copy.Transform = combined;
                    context.PushClip(copy); clips++;
                }
            }
            context.DrawDrawing(drawing);
            while (clips-- > 0) context.Pop();
        }
    }
}
