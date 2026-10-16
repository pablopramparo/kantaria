// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kantor.App.Pixel;

/// <summary>
/// Marco de esquinas escalonadas con borde de color, relieve superior y sombra dura (sin blur ni degradados).
/// Todo se mide en "unidades de píxel" (<see cref="Unit"/>): bordes, muescas y sombra son múltiplos exactos.
/// </summary>
public class PixelFrame : Decorator
{
    public static readonly DependencyProperty FillProperty = Reg(nameof(Fill), typeof(Brush), Brushes.Black);
    public static readonly DependencyProperty BorderBrushProperty = Reg(nameof(BorderBrush), typeof(Brush), Brushes.White);
    public static readonly DependencyProperty HighlightProperty = Reg(nameof(Highlight), typeof(Brush), null);
    public static readonly DependencyProperty ShadowProperty = Reg(nameof(Shadow), typeof(Brush), null);
    public static readonly DependencyProperty UnitProperty = Reg(nameof(Unit), typeof(double), 4.0, measure: true);
    public static readonly DependencyProperty BorderUnitsProperty = Reg(nameof(BorderUnits), typeof(int), 1, measure: true);
    public static readonly DependencyProperty NotchUnitsProperty = Reg(nameof(NotchUnits), typeof(int), 1, measure: true);
    public static readonly DependencyProperty ShadowUnitsProperty = Reg(nameof(ShadowUnits), typeof(int), 1);
    public static readonly DependencyProperty PaddingProperty = Reg(nameof(Padding), typeof(Thickness), new Thickness(12), measure: true);

    static PixelFrame()
    {
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(PixelFrame), new FrameworkPropertyMetadata(true));
    }

    public PixelFrame()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    private static DependencyProperty Reg(string name, Type type, object? defaultValue, bool measure = false) =>
        DependencyProperty.Register(name, type, typeof(PixelFrame), new FrameworkPropertyMetadata(defaultValue,
            measure ? FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender
                    : FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush BorderBrush { get => (Brush)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }

    /// <summary>Línea clara de relieve bajo el borde superior (opcional).</summary>
    public Brush? Highlight { get => (Brush?)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }

    /// <summary>Sombra dura desplazada abajo a la derecha (opcional).</summary>
    public Brush? Shadow { get => (Brush?)GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }

    public double Unit { get => (double)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public int BorderUnits { get => (int)GetValue(BorderUnitsProperty); set => SetValue(BorderUnitsProperty, value); }
    public int NotchUnits { get => (int)GetValue(NotchUnitsProperty); set => SetValue(NotchUnitsProperty, value); }
    public int ShadowUnits { get => (int)GetValue(ShadowUnitsProperty); set => SetValue(ShadowUnitsProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }

    private Thickness Inset => new(Padding.Left + BorderUnits * Unit, Padding.Top + BorderUnits * Unit,
        Padding.Right + BorderUnits * Unit, Padding.Bottom + BorderUnits * Unit);

    protected override Size MeasureOverride(Size constraint)
    {
        var inset = Inset;
        var width = inset.Left + inset.Right;
        var height = inset.Top + inset.Bottom;
        if (Child is null)
        {
            return new Size(width, height);
        }

        Child.Measure(new Size(Math.Max(0, constraint.Width - width), Math.Max(0, constraint.Height - height)));
        return new Size(Child.DesiredSize.Width + width, Child.DesiredSize.Height + height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var inset = Inset;
        Child?.Arrange(new Rect(inset.Left, inset.Top,
            Math.Max(0, arrangeSize.Width - inset.Left - inset.Right),
            Math.Max(0, arrangeSize.Height - inset.Top - inset.Bottom)));
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var u = Unit;
        var notch = NotchUnits * u;
        if (w < 2 * notch + u || h < 2 * notch + u)
        {
            return;
        }

        if (Shadow is { } shadow && ShadowUnits > 0)
        {
            var o = ShadowUnits * u;
            dc.DrawGeometry(shadow, null, Stepped(o, o, w, h, notch, u));
        }

        dc.DrawGeometry(BorderBrush, null, Stepped(0, 0, w, h, notch, u));

        var b = BorderUnits * u;
        if (w > 2 * b + u && h > 2 * b + u)
        {
            var innerNotch = Math.Max(0, notch - b);
            dc.DrawGeometry(Fill, null, Stepped(b, b, w - 2 * b, h - 2 * b, innerNotch, u));
            if (Highlight is { } highlight)
            {
                dc.DrawRectangle(highlight, null, new Rect(b + innerNotch, b, Math.Max(0, w - 2 * b - 2 * innerNotch), u));
            }
        }
    }

    /// <summary>
    /// Rectángulo con cada esquina cortada en escalera: <paramref name="notch"/> es el recorte total por esquina
    /// y <paramref name="unit"/> el tamaño de cada escalón (la silueta típica del pixel art).
    /// </summary>
    internal static Geometry Stepped(double x, double y, double w, double h, double notch, double unit)
    {
        var steps = unit > 0 ? (int)Math.Round(notch / unit) : 0;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (steps <= 0)
            {
                ctx.BeginFigure(new Point(x, y), true, true);
                ctx.LineTo(new Point(x + w, y), false, false);
                ctx.LineTo(new Point(x + w, y + h), false, false);
                ctx.LineTo(new Point(x, y + h), false, false);
            }
            else
            {
                // Esquina superior izquierda, de arriba hacia la izquierda: (n,0) (n,1) (n-1,1) (n-1,2) … (0,n)
                var corner = new List<(double X, double Y)> { (steps * unit, 0) };
                for (var i = 1; i <= steps; i++)
                {
                    corner.Add(((steps - i + 1) * unit, i * unit));
                    corner.Add(((steps - i) * unit, i * unit));
                }

                var points = new List<Point>();
                points.AddRange(corner.Select(p => new Point(x + w - p.X, y + p.Y)));                   // arriba → derecha
                points.AddRange(Enumerable.Reverse(corner).Select(p => new Point(x + w - p.X, y + h - p.Y))); // derecha → abajo
                points.AddRange(corner.Select(p => new Point(x + p.X, y + h - p.Y)));                   // abajo → izquierda
                points.AddRange(Enumerable.Reverse(corner).Select(p => new Point(x + p.X, y + p.Y)));   // izquierda → arriba
                ctx.BeginFigure(points[0], true, true);
                ctx.PolyLineTo(points.Skip(1).ToList(), false, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }
}
