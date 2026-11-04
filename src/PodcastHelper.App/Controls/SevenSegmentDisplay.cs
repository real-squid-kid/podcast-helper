using System.Windows;
using System.Windows.Media;

namespace PodcastHelper.App.Controls;

// Собственные векторные цифры: не требуется поставлять сторонний шрифт.
public sealed class SevenSegmentDisplay : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SevenSegmentDisplay), new FrameworkPropertyMetadata("00:00", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SegmentBrushProperty = DependencyProperty.Register(nameof(SegmentBrush), typeof(Brush), typeof(SevenSegmentDisplay), new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush SegmentBrush { get => (Brush)GetValue(SegmentBrushProperty); set => SetValue(SegmentBrushProperty, value); }
    private static readonly int[] Masks = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F];
    private static readonly Geometry[] Parts = new Rect[] { new(8, 3, 28, 6), new(35, 9, 6, 28), new(35, 43, 6, 28),
        new(8, 71, 28, 6), new(3, 43, 6, 28), new(3, 9, 6, 28), new(8, 37, 28, 6) }.Select(Bevel).ToArray();
    private static Geometry Bevel(Rect rect)
    {
        double x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height, tip = Math.Min(w, h) / 2;
        Point[] points = w > h ? [new(x + tip, y), new(x + w - tip, y), new(x + w, y + h / 2), new(x + w - tip, y + h), new(x + tip, y + h), new(x, y + h / 2)]
            : [new(x + w / 2, y), new(x + w, y + tip), new(x + w, y + h - tip), new(x + w / 2, y + h), new(x, y + h - tip), new(x, y + tip)];
        var geometry = new StreamGeometry(); using (var context = geometry.Open()) { context.BeginFigure(points[0], true, true); context.PolyLineTo(points.Skip(1).ToArray(), true, false); }
        geometry.Freeze(); return geometry;
    }
    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? Text.Length * 34 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 62 : availableSize.Height);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); string text = Text ?? "";
        double units = text.Sum(c => c == ':' ? 18d : 48d);
        if (units == 0 || ActualHeight <= 0 || ActualWidth <= 0) return;
        double scale = Math.Min(ActualWidth / units, ActualHeight / 80);
        dc.PushTransform(new TranslateTransform((ActualWidth - units * scale) / 2, (ActualHeight - 80 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        double x = 0;
        foreach (char character in text)
        {
            if (character == ':')
            { dc.DrawRectangle(SegmentBrush, null, new Rect(x + 5, 23, 6, 6)); dc.DrawRectangle(SegmentBrush, null, new Rect(x + 5, 53, 6, 6)); x += 18; continue; }
            int mask = character is >= '0' and <= '9' ? Masks[character - '0'] : character == '-' ? 0x40 : 0;
            dc.PushTransform(new TranslateTransform(x, 0));
            for (int i = 0; i < Parts.Length; i++)
            {
                bool lit = (mask & 1 << i) != 0;
                if (lit) { dc.PushOpacity(.1); dc.DrawGeometry(null, new Pen(SegmentBrush, 3), Parts[i]); dc.Pop(); }
                dc.PushOpacity(lit ? 1 : 0.035);
                dc.DrawGeometry(SegmentBrush, null, Parts[i]); dc.Pop();
            }
            dc.Pop();
            x += 48;
        }
        dc.Pop(); dc.Pop();
    }
}
