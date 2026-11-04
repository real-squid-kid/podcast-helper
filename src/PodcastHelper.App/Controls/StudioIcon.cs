using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PodcastHelper.App.Controls;

// Собственные пиктограммы в координатах 24×24, без шрифта или внешних ресурсов.
public sealed class StudioIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(StudioIcon), new FrameworkPropertyMetadata("plus", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(StudioIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    protected override Size MeasureOverride(Size size) => new(Math.Min(24, size.Width), Math.Min(24, size.Height));
    protected override void OnRender(DrawingContext dc)
    {
        double scale = Math.Min(ActualWidth, ActualHeight) / 24;
        dc.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Foreground, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        string path = Kind switch
        {
            "plus" => "M5,12 H19 M12,5 V19",
            "history" => "M3,3 H21 V8 H3 Z M5,8 V21 H19 V8 M9,12 H15 M9,16 H15",
            "folder" => "M2,6 H10 L12,9 H22 L19,21 H2 Z M2,9 V3 H10 L12,6 H21 V9",
            "folder-open" => "M2,7 H10 L12,10 H21 L18,22 H2 Z M2,10 V4 H9 M14,2 H22 V10 M22,2 L13,11",
            "export" => "M12,2 V16 M7,11 L12,16 17,11 M3,16 V22 H21 V16",
            "edit" => "M4,20 L5,14 16,3 21,8 10,19 Z M14,5 L19,10 M4,20 L10,19",
            "trash" => "M4,6 H20 M9,6 V3 H15 V6 M6,6 L7,21 H17 L18,6 M10,10 V17 M14,10 V17",
            "up" => "M5,12 L12,5 19,12 M12,5 V21",
            "down" => "M5,12 L12,19 19,12 M12,3 V19",
            "expand" => "M4,9 V4 H9 M15,4 H20 V9 M20,15 V20 H15 M9,20 H4 V15",
            "settings" => "M9,3 L15,3 16,6 19,7 22,10 20,13 20,17 16,18 14,21 10,21 8,18 4,17 4,13 2,10 5,7 8,6 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",
            "play" => "M8,4 L20,12 8,20 Z",
            "pause" => "M7,4 H10 V20 H7 Z M15,4 H18 V20 H15 Z",
            "stop" => "M5,5 H19 V19 H5 Z",
            "next" => "M4,5 L14,12 4,19 Z M18,5 V19",
            "undo" => "M9,5 L3,10 9,15 M4,10 H15 C23,10 23,21 14,21 H11",
            "check" => "M4,12 L10,18 21,6",
            "close" => "M5,5 L19,19 M19,5 L5,19",
            "clock" => "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M12,6 V12 L16,14",
            "recover" => "M3,4 V10 H9 M4,10 A9,9 0 1 1 5,19 M12,7 V13 L16,15",
            "save" => "M4,3 H17 L21,7 V21 H3 V3 Z M7,3 V10 H17 V3 M7,21 V15 H17 V21",
            "mouse" => "M6,9 C6,1 18,1 18,9 V16 C18,24 6,24 6,16 Z M12,2 V10 M6,10 H18",
            "question" => "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M8,8 C8,3 18,5 15,10 L12,13 V15 M12,18 V19",
            "clap" => "M3,10 H21 V21 H3 Z M3,10 L1,5 19,1 21,6 Z M5,4 L10,8 M12,2 L17,6 M3,14 H21",
            "flag" => "M5,22 V3 M5,3 H19 L16,8 19,13 H5",
            _ => "M5,5 H19 V19 H5 Z"
        };
        dc.DrawGeometry(null, pen, Geometry.Parse(path)); dc.Pop(); dc.Pop();
    }
}

public sealed class PanelScrew : FrameworkElement
{
    protected override Size MeasureOverride(Size size) => new(14, 14);
    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(14, 18, 20)), null, new Point(center.X, center.Y + 1), 7, 7);
        dc.DrawEllipse(new LinearGradientBrush(Color.FromRgb(153, 156, 148), Color.FromRgb(51, 58, 58), 90), new Pen(Brushes.Black, .7), center, 5.5, 5.5);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(24, 30, 31)), 1.7), new(center.X - 3, center.Y + 2), new(center.X + 3, center.Y - 2));
    }
}
