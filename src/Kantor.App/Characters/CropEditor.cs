// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kantor.App.Pixel;
using Kantor.Core.Characters;

namespace Kantor.App.Characters;

/// <summary>
/// Muestra una imagen y un recuadro cuadrado movible (la cara). Mouse: arrastrar adentro mueve, arrastrar una
/// esquina o la rueda cambian el tamaño. Teclado: flechas mueven, + − cambian el tamaño (Mayús: más rápido).
/// La geometría vive en <see cref="SquareCrop"/>; acá solo se traduce entre pantalla e imagen.
/// </summary>
public sealed class CropEditor : FrameworkElement
{
    private const double HandleSize = 14;

    private readonly BitmapSource _source;
    private SquareCrop _crop;
    private Point? _dragStart;
    private SquareCrop _dragOrigin;
    private bool _resizing;

    public CropEditor(BitmapSource source, SquareCrop crop, double maxWidth, double maxHeight)
    {
        _source = source;
        _crop = crop.Clamp(source.PixelWidth, source.PixelHeight);
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.SizeAll;
        var scale = Math.Min(maxWidth / source.PixelWidth, maxHeight / source.PixelHeight);
        Width = Math.Max(1, source.PixelWidth * scale);
        Height = Math.Max(1, source.PixelHeight * scale);
        Tag = "crop";
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public SquareCrop Crop => _crop;

    public event EventHandler? CropChanged;

    private double Scale => Width / _source.PixelWidth;

    private Rect CropRect => new(_crop.X * Scale, _crop.Y * Scale, _crop.Size * Scale, _crop.Size * Scale);

    private void SetCrop(SquareCrop crop)
    {
        crop = crop.Clamp(_source.PixelWidth, _source.PixelHeight);
        if (crop == _crop)
        {
            return;
        }

        _crop = crop;
        InvalidateVisual();
        CropChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var all = new Rect(0, 0, Width, Height);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1B, 0x10, 0x40)), null, all);
        dc.DrawImage(_source, all);

        var rect = CropRect;
        var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(all),
            new RectangleGeometry(rect));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0xB0, 0x05, 0x07, 0x18)), null, shade);

        var gold = PixelUi.Brush(IsKeyboardFocused ? "GoldBrush" : "TextBrush");
        dc.DrawRectangle(null, new Pen(gold, 2), rect);
        foreach (var corner in Corners(rect))
        {
            dc.DrawRectangle(gold, null,
                new Rect(corner.X - HandleSize / 2, corner.Y - HandleSize / 2, HandleSize, HandleSize));
        }

        dc.DrawRectangle(null, new Pen(PixelUi.Brush("InkBrush"), 1), all);
    }

    private static Point[] Corners(Rect r) => [r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight];

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        var rect = CropRect;
        _resizing = Corners(rect).Any(c => Math.Abs(c.X - p.X) <= HandleSize && Math.Abs(c.Y - p.Y) <= HandleSize);
        if (_resizing || rect.Contains(p))
        {
            _dragStart = p;
            _dragOrigin = _crop;
            CaptureMouse();
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragStart is not { } start)
        {
            var near = Corners(CropRect).Any(c => Math.Abs(c.X - p.X) <= HandleSize && Math.Abs(c.Y - p.Y) <= HandleSize);
            Cursor = near ? Cursors.SizeNWSE : Cursors.SizeAll;
            return;
        }

        if (_resizing)
        {
            // el centro queda quieto; el lado sale de la distancia del mouse al centro
            var cx = (_dragOrigin.X + _dragOrigin.Size / 2) * Scale;
            var cy = (_dragOrigin.Y + _dragOrigin.Size / 2) * Scale;
            var half = Math.Max(Math.Abs(p.X - cx), Math.Abs(p.Y - cy));
            SetCrop(_dragOrigin.Resized(half * 2 / Scale, _source.PixelWidth, _source.PixelHeight));
        }
        else
        {
            SetCrop(_dragOrigin.Moved((p.X - start.X) / Scale, (p.Y - start.Y) / Scale,
                _source.PixelWidth, _source.PixelHeight));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragStart = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Zoom(e.Delta > 0 ? 1.08 : 1 / 1.08);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var fast = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var step = _crop.Size * (fast ? 0.10 : 0.02);
        switch (e.Key)
        {
            case Key.Left:
                SetCrop(_crop.Moved(-step, 0, _source.PixelWidth, _source.PixelHeight));
                break;
            case Key.Right:
                SetCrop(_crop.Moved(step, 0, _source.PixelWidth, _source.PixelHeight));
                break;
            case Key.Up:
                SetCrop(_crop.Moved(0, -step, _source.PixelWidth, _source.PixelHeight));
                break;
            case Key.Down:
                SetCrop(_crop.Moved(0, step, _source.PixelWidth, _source.PixelHeight));
                break;
            case Key.Add or Key.OemPlus:
                Zoom(fast ? 1.25 : 1.08);
                break;
            case Key.Subtract or Key.OemMinus:
                Zoom(fast ? 0.8 : 1 / 1.08);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Zoom(double factor) =>
        SetCrop(_crop.Resized(_crop.Size * factor, _source.PixelWidth, _source.PixelHeight));

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
}
