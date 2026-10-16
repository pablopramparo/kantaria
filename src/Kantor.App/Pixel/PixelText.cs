// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Kantor.App.Pixel;

/// <summary>
/// Texto de la tipografía pixel con sombra dura y contorno opcional. Solo para títulos y elementos destacados:
/// el texto largo usa la tipografía legible del tema.
/// </summary>
public class PixelText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = Reg(nameof(Text), typeof(string), string.Empty);
    public static readonly DependencyProperty ForegroundProperty = Reg(nameof(Foreground), typeof(Brush), Brushes.White, false);
    public static readonly DependencyProperty ShadowProperty = Reg(nameof(Shadow), typeof(Brush), null, false);
    public static readonly DependencyProperty OutlineProperty = Reg(nameof(Outline), typeof(Brush), null, false);
    public static readonly DependencyProperty FontSizeProperty = Reg(nameof(FontSize), typeof(double), 16.0);
    public static readonly DependencyProperty ShadowOffsetProperty = Reg(nameof(ShadowOffset), typeof(double), 4.0);
    public static readonly DependencyProperty OutlineSizeProperty = Reg(nameof(OutlineSize), typeof(double), 4.0);
    public static readonly DependencyProperty FontWeightProperty = Reg(nameof(FontWeight), typeof(FontWeight), FontWeights.Normal);

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(nameof(FontFamily),
        typeof(FontFamily), typeof(PixelText),
        new FrameworkPropertyMetadata(new FontFamily("Segoe UI"), FrameworkPropertyMetadataOptions.AffectsMeasure
                                                                    | FrameworkPropertyMetadataOptions.AffectsRender));

    public PixelText()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    private static DependencyProperty Reg(string name, Type type, object? value, bool measure = true) =>
        DependencyProperty.Register(name, type, typeof(PixelText), new FrameworkPropertyMetadata(value,
            measure ? FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender
                    : FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush? Shadow { get => (Brush?)GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }
    public Brush? Outline { get => (Brush?)GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public double ShadowOffset { get => (double)GetValue(ShadowOffsetProperty); set => SetValue(ShadowOffsetProperty, value); }
    public double OutlineSize { get => (double)GetValue(OutlineSizeProperty); set => SetValue(OutlineSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    private FormattedText Format(Brush brush)
    {
        var ft = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (Text.Contains('\n'))
        {
            ft.MaxTextWidth = ft.Width;
            ft.TextAlignment = TextAlignment.Center;
        }
        else if (!double.IsInfinity(MaxWidth))
        {
            // nombre largo: una sola línea, recortada con puntos suspensivos
            var pad = (Outline is not null ? OutlineSize * 2 : 0) + (Shadow is not null ? ShadowOffset : 0);
            ft.MaxTextWidth = Math.Max(1, MaxWidth - pad);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }

        return ft;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return new Size(0, 0);
        }

        var ft = Format(Foreground);
        var pad = Outline is not null ? OutlineSize : 0;
        return new Size(ft.WidthIncludingTrailingWhitespace + 2 * pad + (Shadow is not null ? ShadowOffset : 0),
            ft.Height + 2 * pad + (Shadow is not null ? ShadowOffset : 0));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        var pad = Outline is not null ? OutlineSize : 0;
        var origin = new Point(pad, pad);

        if (Shadow is { } shadow)
        {
            var o = ShadowOffset;
            DrawOutlined(dc, shadow, origin + new Vector(o, o), pad);
        }

        if (Outline is { } outline)
        {
            DrawOutlined(dc, outline, origin, pad);
        }

        dc.DrawText(Format(Foreground), origin);
    }

    /// <summary>Contorno duro: el texto repetido en las ocho direcciones, sin suavizado.</summary>
    private void DrawOutlined(DrawingContext dc, Brush brush, Point origin, double size)
    {
        var ft = Format(brush);
        if (size <= 0)
        {
            dc.DrawText(ft, origin);
            return;
        }

        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (dx != 0 || dy != 0)
                {
                    dc.DrawText(ft, origin + new Vector(dx * size, dy * size));
                }
            }
        }
    }
}
