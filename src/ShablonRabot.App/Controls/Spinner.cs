using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ShablonRabot.App.Controls;

/// <summary>Спиннер 16 px: дуга 270°, вращение 1 с. Если анимации в Windows отключены — статичная иконка.</summary>
public sealed class Spinner : Grid
{
    public Spinner()
    {
        Width = Height = 16;
        if (!SystemParameters.ClientAreaAnimation)
        {
            var icon = new TextBlock { Text = "", FontSize = 16, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent.Default");
            Children.Add(icon);
            return;
        }
        var arc = new Path
        {
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M 8,1 A 7,7 0 1 1 1,8"),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(),
        };
        arc.SetResourceReference(Shape.StrokeProperty, "Brush.Accent.Default");
        Children.Add(arc);
        Loaded += (_, _) => arc.RenderTransform.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
    }
}
