using System.Windows;
using System.Windows.Media;

namespace VibePlayer.Controls;

/// <summary>Draws a 24x24 icon geometry scaled to the element's size.</summary>
public sealed class IconView : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(IconView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(IconView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IconView()
    {
        Width = 24;
        Height = 24;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var data = Data;
        if (data == null) return;

        var scale = Math.Min(ActualWidth, ActualHeight) / 24.0;
        dc.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(Fill, null, data);
        dc.Pop();
        dc.Pop();
    }
}
