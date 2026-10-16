// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Media;
using Kantor.Core.Results;

namespace Kantor.App.Pixel;

/// <summary>
/// Personaje de cuerpo entero, suelto sobre el escenario (sin marco). Recibe PNG con transparencia de cualquier
/// proporción y tamaño: se escala sin deformarlo ni suavizarlo, anclado por los pies al centro de su zona, y puede
/// sobresalir por arriba (<see cref="Overflow"/>) y un poco por los costados sin tapar el resto de la pantalla.
/// Debajo de los pies dibuja una sombra pixelada discreta. Sin imagen usa un personaje provisional.
/// </summary>
public class PixelCharacter : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source),
        typeof(ImageSource), typeof(PixelCharacter), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent),
        typeof(Brush), typeof(PixelCharacter), new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OverflowProperty = DependencyProperty.Register(nameof(Overflow),
        typeof(double), typeof(PixelCharacter), new FrameworkPropertyMetadata(0.12, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VisualScaleProperty = DependencyProperty.Register(nameof(VisualScale),
        typeof(double), typeof(PixelCharacter), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GroundOffsetProperty = DependencyProperty.Register(nameof(GroundOffset),
        typeof(double), typeof(PixelCharacter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush ShadowBrush = Frozen(Color.FromArgb(0x8C, 0x03, 0x04, 0x10));

    // Personaje provisional de 16 × 22: H pelo, S piel, E ojos, B remera (color del jugador), P pantalón, F zapatillas, M micrófono.
    private static readonly string[] Placeholder =
    [
        "................",
        "......HHHH......",
        ".....HHHHHH.....",
        ".....HSSSSH.....",
        ".....SESSES.....",
        ".....SSSSSS.....",
        "......SMMS......",
        ".......SS.......",
        "....BBBBBBBB....",
        "...BBBBBBBBBB...",
        "..SBBBBBBBBBBS..",
        "..SBBBBBBBBBBSM.",
        "..SBBBBBBBBBB.M.",
        "....BBBBBBBB....",
        "....PPPPPPPP....",
        "....PPPPPPPP....",
        "....PPP..PPP....",
        "....PPP..PPP....",
        "....PPP..PPP....",
        "....PPP..PPP....",
        "...FFFF..FFFF...",
        "...FFFF..FFFF...",
    ];

    public PixelCharacter()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    /// <summary>PNG transparente individual (cualquier proporción). <c>null</c> usa el personaje provisional.</summary>
    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    /// <summary>Multiplicador opcional del tamaño ajustado (1 = sin cambio); para personajes anchos.</summary>
    public double VisualScale { get => (double)GetValue(VisualScaleProperty); set => SetValue(VisualScaleProperty, value); }

    /// <summary>Desplazamiento opcional de la línea de pies (positivo = más abajo).</summary>
    public double GroundOffset { get => (double)GetValue(GroundOffsetProperty); set => SetValue(GroundOffsetProperty, value); }

    /// <summary>Cuánto puede sobresalir por arriba de su zona (fracción de la altura; ajustable por personaje).</summary>
    public double Overflow { get => (double)GetValue(OverflowProperty); set => SetValue(OverflowProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 8 || h < 24)
        {
            return;
        }

        // sombra: elipse escalonada de tres filas, justo bajo los pies
        const double unit = 5;
        var baseY = h - unit + GroundOffset; // la sombra acompaña a la línea de pies
        var cx = w / 2;
        dc.DrawRectangle(ShadowBrush, null, new Rect(Snap(cx - w * 0.16), baseY - 2 * unit, Snap(w * 0.32), unit));
        dc.DrawRectangle(ShadowBrush, null, new Rect(Snap(cx - w * 0.26), baseY - unit, Snap(w * 0.52), unit));
        dc.DrawRectangle(ShadowBrush, null, new Rect(Snap(cx - w * 0.16), baseY, Snap(w * 0.32), unit));

        if (Source is { } image)
        {
            var p = CharacterPlacement.Compute(image.Width, image.Height, w, h, VisualScale, GroundOffset, Overflow);
            dc.DrawImage(image, new Rect(p.X, p.Y, p.Width, p.Height));
            return;
        }

        var feetY = h - CharacterPlacement.ShadowBand + GroundOffset;
        var boxW = w * 1.15 * Math.Max(0.01, VisualScale);
        var boxH = feetY * (1 + Math.Max(0, Overflow));
        DrawPlaceholder(dc, cx, feetY, boxW, boxH);
    }

    private void DrawPlaceholder(DrawingContext dc, double cx, double feetY, double boxW, double boxH)
    {
        var rows = Placeholder.Length;
        var cols = Placeholder[0].Length;
        var cell = Math.Floor(Math.Min(boxW / cols, boxH / rows));
        if (cell < 1)
        {
            return;
        }

        var left = Math.Round(cx - cols * cell / 2);
        var top = Math.Round(feetY - rows * cell);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var brush = Placeholder[y][x] switch
                {
                    'H' => PaletteHair,
                    'S' => PaletteSkin,
                    'E' => PaletteInk,
                    'B' => Accent,
                    'P' => PalettePants,
                    'F' => PaletteShoe,
                    'M' => PaletteMic,
                    _ => null,
                };
                if (brush is not null)
                {
                    dc.DrawRectangle(brush, null, new Rect(left + x * cell, top + y * cell, cell, cell));
                }
            }
        }
    }

    private static readonly Brush PaletteHair = Frozen(Color.FromRgb(0x3B, 0x24, 0x14));
    private static readonly Brush PaletteSkin = Frozen(Color.FromRgb(0xF2, 0xC2, 0x9B));
    private static readonly Brush PaletteInk = Frozen(Color.FromRgb(0x10, 0x10, 0x20));
    private static readonly Brush PalettePants = Frozen(Color.FromRgb(0x1B, 0x20, 0x4A));
    private static readonly Brush PaletteShoe = Frozen(Color.FromRgb(0xF4, 0xF6, 0xFF));
    private static readonly Brush PaletteMic = Frozen(Color.FromRgb(0xB8, 0xBC, 0xCC));

    private static double Snap(double v) => Math.Round(v / 5) * 5;

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
