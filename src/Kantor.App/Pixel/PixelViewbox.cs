// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kantor.App.Pixel;

/// <summary>
/// Un <see cref="Viewbox"/> para pixel art: escala la composición (uniforme y centrada) pero cuantiza el factor para
/// que cada unidad del diseño caiga en una cantidad entera de píxeles físicos del monitor. Con un factor continuo,
/// los escalones de 4 unidades (marcos, contornos, sombras duras) salían de 4 o 5 píxeles según la posición y
/// el borde "temblaba". La cuantización es a múltiplos de 0,25 píxeles físicos por unidad (así 4 unidades son
/// siempre una cantidad entera de píxeles), calculada con el DPI del monitor actual: al pasar de un monitor a otro
/// con otra escala se vuelve a calcular.
/// </summary>
public sealed class PixelViewbox : Decorator
{
    /// <summary>Paso de la cuantización, en píxeles físicos por unidad de diseño.</summary>
    public const double Step = 0.25;

    private double _scale = 1;
    private Size _designSize;

    /// <summary>
    /// Factor de escala (físico por unidad de diseño) cuantizado: nunca mayor que el que cabe y nunca menor que
    /// un paso. Si lo que cabe es menor que un paso (ventana diminuta) se usa el valor continuo.
    /// </summary>
    public static double Quantize(double physicalScale)
    {
        if (physicalScale <= Step)
        {
            return physicalScale;
        }

        return Math.Floor(physicalScale / Step + 1e-9) * Step;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
        {
            return new Size(0, 0);
        }

        // El contenido define su tamaño de diseño (ancho fijo; el alto puede crecer).
        Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _designSize = Child.DesiredSize;
        var available = new Size(
            double.IsInfinity(constraint.Width) ? _designSize.Width : constraint.Width,
            double.IsInfinity(constraint.Height) ? _designSize.Height : constraint.Height);
        _scale = ComputeScale(available);
        return new Size(
            double.IsInfinity(constraint.Width) ? _designSize.Width * _scale : constraint.Width,
            double.IsInfinity(constraint.Height) ? _designSize.Height * _scale : constraint.Height);
    }

    protected override Size ArrangeOverride(Size arrange)
    {
        if (Child is null)
        {
            return arrange;
        }

        _scale = ComputeScale(arrange);
        Child.Arrange(new Rect(new Point(0, 0), _designSize));

        // Se centra y se redondea a píxeles físicos para que el origen tampoco caiga entre dos píxeles.
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var offsetX = Snap((arrange.Width - _designSize.Width * _scale) / 2, dpi);
        var offsetY = Snap((arrange.Height - _designSize.Height * _scale) / 2, dpi);
        Child.RenderTransform = new MatrixTransform(_scale, 0, 0, _scale, offsetX, offsetY);
        return arrange;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
        InvalidateArrange();
    }

    private double ComputeScale(Size available)
    {
        if (_designSize.Width <= 0 || _designSize.Height <= 0)
        {
            return 1;
        }

        var fit = Math.Min(available.Width / _designSize.Width, available.Height / _designSize.Height);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var physical = fit * dpi;
        return Quantize(physical) / dpi;
    }

    private static double Snap(double dips, double dpi) => Math.Floor(dips * dpi) / dpi;
}
