// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Kantor.App.Pixel;

/// <summary>
/// Carril e indicador de posición de una barra de desplazamiento vertical, dibujados con el estilo pixel.
/// Va dentro de la plantilla de un <see cref="ScrollBar"/> y lo maneja por <c>TemplatedParent</c>. Reemplaza al
/// <c>Track</c> de WPF porque este fuerza un pulgar de ~8 px con listas largas: acá el pulgar tiene un largo
/// mínimo visible y se agarra, se arrastra y el carril salta por página.
/// </summary>
public sealed class PixelScrollTrack : FrameworkElement
{
    private const double MinThumb = 56;
    private static readonly Brush TrackFill = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x0D, 0x22)));
    private static readonly Brush TrackBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x70)));
    private static readonly Brush ThumbFill = Freeze(new SolidColorBrush(Color.FromRgb(0x19, 0xD3, 0xFF)));
    private static readonly Brush ThumbHot = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC9, 0x33)));
    private static readonly Brush ThumbEdge = Freeze(new SolidColorBrush(Color.FromRgb(0x05, 0x07, 0x18)));
    private static readonly Brush ThumbShine = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));

    private bool _hover;
    private bool _dragging;
    private double _grab;
    private ScrollBar? _bar;

    public PixelScrollTrack()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        Detach();
        _bar = TemplatedParent as ScrollBar;
        if (_bar is null)
        {
            return;
        }

        foreach (var property in new[] { RangeBase.ValueProperty, RangeBase.MinimumProperty, RangeBase.MaximumProperty,
                     ScrollBar.ViewportSizeProperty })
        {
            DependencyPropertyDescriptor.FromProperty(property, typeof(ScrollBar))?.AddValueChanged(_bar, OnBarChanged);
        }

        // El primer dibujo ocurrió antes de enganchar la barra (sin pulgar): se vuelve a pintar con sus valores.
        InvalidateVisual();
    }

    private void Detach()
    {
        if (_bar is null)
        {
            return;
        }

        foreach (var property in new[] { RangeBase.ValueProperty, RangeBase.MinimumProperty, RangeBase.MaximumProperty,
                     ScrollBar.ViewportSizeProperty })
        {
            DependencyPropertyDescriptor.FromProperty(property, typeof(ScrollBar))?.RemoveValueChanged(_bar, OnBarChanged);
        }

        _bar = null;
    }

    private void OnBarChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>Rectángulo del pulgar según el valor actual; <c>null</c> si no hay nada que desplazar.</summary>
    private Rect? ThumbRect()
    {
        if (_bar is null)
        {
            return null;
        }

        var range = _bar.Maximum - _bar.Minimum;
        var height = ActualHeight - 6;
        if (range <= 0 || height <= 0)
        {
            return null;
        }

        var total = range + _bar.ViewportSize;
        var length = Math.Clamp(height * _bar.ViewportSize / total, Math.Min(MinThumb, height), height);
        var offset = (_bar.Value - _bar.Minimum) / range * (height - length);
        return new Rect(3, 3 + offset, Math.Max(0, ActualWidth - 6), length);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var track = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawGeometry(TrackBorder, null, PixelFrame.Stepped(track.Left, track.Top, track.Width, track.Height, 2, 2));
        dc.DrawGeometry(TrackFill, null, PixelFrame.Stepped(2, 2, track.Width - 4, track.Height - 4, 0, 2));

        if (ThumbRect() is not { } thumb)
        {
            return;
        }

        var hot = _hover || _dragging;
        dc.DrawGeometry(ThumbEdge, null, PixelFrame.Stepped(thumb.Left, thumb.Top, thumb.Width, thumb.Height, 2, 2));
        dc.DrawGeometry(hot ? ThumbHot : ThumbFill, null,
            PixelFrame.Stepped(thumb.Left + 2, thumb.Top + 2, thumb.Width - 4, thumb.Height - 4, 0, 2));
        dc.DrawRectangle(ThumbShine, null, new Rect(thumb.Left + 4, thumb.Top + 4, Math.Max(0, thumb.Width - 8), 2));
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        _hover = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_bar is null)
        {
            return;
        }

        var y = e.GetPosition(this).Y;
        if (ThumbRect() is { } thumb && y >= thumb.Top && y <= thumb.Bottom)
        {
            _dragging = true;
            _grab = y - thumb.Top;
            CaptureMouse();
        }
        else if (ThumbRect() is { } other)
        {
            // clic en el carril: una página hacia donde se hizo clic
            var up = y < other.Top;
            var target = Math.Clamp(_bar.Value + (up ? -1 : 1) * Math.Max(1, _bar.ViewportSize), _bar.Minimum, _bar.Maximum);
            Raise(up ? ScrollEventType.LargeDecrement : ScrollEventType.LargeIncrement, target);
        }

        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging || _bar is null || ThumbRect() is not { } thumb)
        {
            return;
        }

        var height = ActualHeight - 6;
        var room = height - thumb.Height;
        if (room > 0)
        {
            var offset = Math.Clamp(e.GetPosition(this).Y - _grab - 3, 0, room);
            Raise(ScrollEventType.ThumbTrack, _bar.Minimum + offset / room * (_bar.Maximum - _bar.Minimum));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            if (_bar is not null)
            {
                Raise(ScrollEventType.EndScroll, _bar.Value);
            }
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Mueve el contenido. La barra de un <c>ScrollViewer</c> es de solo lectura (su valor sale del visor), así
    /// que no alcanza con cambiar <c>Value</c> ni con avisar un evento: se le pide el desplazamiento directamente
    /// al <c>ScrollViewer</c> dueño de la plantilla. Si la barra está suelta, se cambia su valor.
    /// </summary>
    private void Raise(ScrollEventType type, double value)
    {
        if (_bar is null)
        {
            return;
        }

        var clamped = Math.Clamp(value, _bar.Minimum, _bar.Maximum);
        if (_bar.TemplatedParent is ScrollViewer viewer)
        {
            switch (type)
            {
                case ScrollEventType.ThumbTrack or ScrollEventType.ThumbPosition:
                    viewer.ScrollToVerticalOffset(clamped);
                    break;
                case ScrollEventType.LargeDecrement:
                    viewer.PageUp();
                    break;
                case ScrollEventType.LargeIncrement:
                    viewer.PageDown();
                    break;
            }
        }
        else if (type != ScrollEventType.EndScroll)
        {
            _bar.Value = clamped;
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
