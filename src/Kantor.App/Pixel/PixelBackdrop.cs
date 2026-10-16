// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Kantor.App.Pixel;

/// <summary>
/// Fondo de escenario dibujado a muy baja resolución (180 píxeles lógicos de alto) y ampliado sin suavizado:
/// todos los "píxeles" tienen el mismo tamaño. Reflectores, estrellas parpadeantes y confeti a 15 cuadros por
/// segundo, solo mientras el elemento está visible.
/// </summary>
public sealed class PixelBackdrop : FrameworkElement
{
    private const int LogicalHeight = 180;
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(66);

    private static readonly Color SkyTop = Color.FromRgb(0x07, 0x0A, 0x24);
    private static readonly Color SkyBottom = Color.FromRgb(0x24, 0x12, 0x52);
    private static readonly Color[] ConfettiColors =
    [
        Color.FromRgb(0x19, 0xD3, 0xFF), Color.FromRgb(0xFF, 0x3D, 0xB8), Color.FromRgb(0xFF, 0xC9, 0x33),
        Color.FromRgb(0xF4, 0xF6, 0xFF), Color.FromRgb(0x7C, 0x5C, 0xFF),
    ];

    private readonly DrawingVisual _visual = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(double Start, double X, double Y, int Count)> _bursts = [];
    private RenderTargetBitmap? _bitmap;
    private int _logicalWidth;
    private Color[] _accents = [Color.FromRgb(0x19, 0xD3, 0xFF)];

    public PixelBackdrop()
    {
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_visual, EdgeMode.Aliased);
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = Frame };
        _timer.Tick += (_, _) => Redraw();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _timer.Start();
                Redraw();
            }
            else
            {
                _timer.Stop();
            }
        };
        SizeChanged += (_, _) => Redraw();
    }

    /// <summary>Un reflector por color, repartidos de izquierda a derecha.</summary>
    public IReadOnlyList<Color> Accents
    {
        get => _accents;
        set
        {
            _accents = value.Count > 0 ? [.. value] : [Color.FromRgb(0x19, 0xD3, 0xFF)];
            Redraw();
        }
    }

    /// <summary>Lanza una ráfaga de confeti desde un punto (0–1 de cada eje).</summary>
    public void Burst(double x = 0.5, double y = 0.45, int count = 60)
    {
        _bursts.Add((_clock.Elapsed.TotalSeconds, x, y, count));
        if (_bursts.Count > 8)
        {
            _bursts.RemoveAt(0);
        }
    }

    private void Redraw()
    {
        if (ActualWidth < 1 || ActualHeight < 1)
        {
            return;
        }

        var scale = ActualHeight / LogicalHeight;
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth / scale));
        if (_bitmap is null || width != _logicalWidth)
        {
            _logicalWidth = width;
            _bitmap = new RenderTargetBitmap(width, LogicalHeight, 96, 96, PixelFormats.Pbgra32);
        }

        using (var dc = _visual.RenderOpen())
        {
            Draw(dc, width, LogicalHeight, _clock.Elapsed.TotalSeconds);
        }

        _bitmap.Clear();
        _bitmap.Render(_visual);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_bitmap is null)
        {
            return;
        }

        var scale = ActualHeight / LogicalHeight;
        dc.DrawImage(_bitmap, new Rect(0, 0, _logicalWidth * scale, LogicalHeight * scale));
    }

    // ---- Dibujo a resolución lógica -----------------------------------------------------------------------

    private void Draw(DrawingContext dc, int w, int h, double t)
    {
        var floor = (int)(h * 0.80);

        // cielo en bandas planas (sin degradado)
        const int bands = 12;
        for (var i = 0; i < bands; i++)
        {
            var y0 = floor * i / bands;
            var y1 = floor * (i + 1) / bands;
            dc.DrawRectangle(new SolidColorBrush(Lerp(SkyTop, SkyBottom, i / (double)(bands - 1))), null,
                new Rect(0, y0, w, y1 - y0));
        }

        // piso de escenario con perspectiva
        dc.DrawRectangle(Brush(0x0C, 0x08, 0x26), null, new Rect(0, floor, w, h - floor));
        dc.DrawRectangle(Brush(0x4A, 0x32, 0x9A), null, new Rect(0, floor, w, 1));
        var linePen = new Pen(Brush(0x24, 0x18, 0x5A), 1);
        for (var i = -12; i <= 12; i++)
        {
            dc.DrawLine(linePen, new Point(w / 2 + i * 5, floor + 1), new Point(w / 2 + i * 22, h));
        }

        for (var row = 1; row <= 3; row++)
        {
            var y = floor + (int)((h - floor) * Math.Pow(row / 4.0, 1.6));
            dc.DrawRectangle(Brush(0x24, 0x18, 0x5A), null, new Rect(0, y, w, 1));
        }

        Stars(dc, w, floor, t);

        // reflectores
        var n = _accents.Length;
        for (var i = 0; i < n; i++)
        {
            var accent = _accents[i];
            var x0 = (int)(w * (i + 0.5) / n + Math.Round(Math.Sin(t * 0.7 + i * 2.1) * 5));
            var spread = (int)(h * 0.34);
            Cone(dc, x0, floor, 3, spread, Color.FromArgb(0x30, accent.R, accent.G, accent.B));
            Cone(dc, x0, floor, 1, spread / 2, Color.FromArgb(0x38, accent.R, accent.G, accent.B));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x50, accent.R, accent.G, accent.B)), null,
                new Point(x0, floor + 7), spread, 6);
        }

        Confetti(dc, w, h, t);
        Bursts(dc, w, h, t);
    }

    private static void Cone(DrawingContext dc, int x, int floor, int topHalf, int bottomHalf, Color color)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x - topHalf, 0), true, true);
            ctx.LineTo(new Point(x + topHalf, 0), false, false);
            ctx.LineTo(new Point(x + bottomHalf, floor), false, false);
            ctx.LineTo(new Point(x - bottomHalf, floor), false, false);
        }

        g.Freeze();
        dc.DrawGeometry(new SolidColorBrush(color), null, g);
    }

    private static void Stars(DrawingContext dc, int w, int floor, double t)
    {
        for (var i = 0; i < 46; i++)
        {
            var x = (int)(Rand(i, 1) * w);
            var y = (int)(Rand(i, 2) * floor * 0.7);
            var phase = (t * (0.6 + Rand(i, 3)) + Rand(i, 4) * 10) % 2.0;
            var big = phase < 0.25;
            var on = phase < 1.4;
            if (!on)
            {
                continue;
            }

            var color = i % 5 == 0 ? Color.FromRgb(0xFF, 0xC9, 0x33) : Color.FromRgb(0xF4, 0xF6, 0xFF);
            var brush = new SolidColorBrush(color);
            dc.DrawRectangle(brush, null, new Rect(x, y, 1, 1));
            if (big)
            {
                dc.DrawRectangle(brush, null, new Rect(x - 1, y, 3, 1));
                dc.DrawRectangle(brush, null, new Rect(x, y - 1, 1, 3));
            }
        }
    }

    private static void Confetti(DrawingContext dc, int w, int h, double t)
    {
        for (var i = 0; i < 70; i++)
        {
            var speed = 8 + Rand(i, 11) * 14;
            var y = (Rand(i, 12) * (h + 20) + t * speed) % (h + 20) - 10;
            var x = Rand(i, 13) * w + Math.Sin(t * (0.8 + Rand(i, 14)) + i) * 4;
            var flip = ((int)(t * 4) + i) % 2 == 0;
            var color = ConfettiColors[i % ConfettiColors.Length];
            dc.DrawRectangle(new SolidColorBrush(color), null,
                new Rect(Math.Floor(x), Math.Floor(y), flip ? 2 : 1, flip ? 1 : 2));
        }
    }

    private void Bursts(DrawingContext dc, int w, int h, double t)
    {
        for (var b = _bursts.Count - 1; b >= 0; b--)
        {
            var (start, ox, oy, count) = _bursts[b];
            var age = t - start;
            if (age > 1.6)
            {
                _bursts.RemoveAt(b);
                continue;
            }

            for (var i = 0; i < count; i++)
            {
                var angle = Rand(i + b * 97, 21) * Math.PI * 2;
                var speed = 25 + Rand(i + b * 97, 22) * 70;
                var x = w * ox + Math.Cos(angle) * speed * age;
                var y = h * oy + Math.Sin(angle) * speed * age + 40 * age * age;
                var color = ConfettiColors[i % ConfettiColors.Length];
                dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(Math.Floor(x), Math.Floor(y), 2, 2));
            }
        }
    }

    private static double Rand(int i, int k)
    {
        unchecked
        {
            var x = (uint)(i * 7919 + k * 104729 + 12345);
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            x *= 2654435761u;
            x ^= x >> 15;
            return x / (double)uint.MaxValue;
        }
    }

    private static Color Lerp(Color a, Color b, double f) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
}
