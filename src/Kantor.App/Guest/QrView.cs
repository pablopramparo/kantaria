// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Media;

namespace Kantor.App.Guest;

/// <summary>
/// Dibuja un código QR con módulos de tamaño entero (nítido, sin suavizado) sobre fondo claro con su zona de
/// silencio de 4 módulos. Oscuro sobre claro y con buen contraste: lo que necesita la cámara de un celular.
/// </summary>
public sealed class QrView : FrameworkElement
{
    private const int Quiet = 4;

    private static readonly Brush Light = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xFF)));
    private static readonly Brush Dark = Freeze(new SolidColorBrush(Color.FromRgb(0x05, 0x07, 0x18)));

    private bool[,]? _modules;

    public QrView()
    {
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    /// <summary>Lado fijo del dibujo (en unidades de diseño).</summary>
    public double Side { get; set; } = 200;

    /// <summary>Módulos de tamaño entero (nítido); falso permite un QR más chico a costa de bordes algo desparejos.</summary>
    public bool IntegerCells { get; set; } = true;

    public bool[,]? Modules
    {
        get => _modules;
        set
        {
            _modules = value;
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(Side, Side);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Light, null, new Rect(0, 0, Side, Side));
        if (_modules is null)
        {
            return;
        }

        var n = _modules.GetLength(0);
        var raw = Side / (n + 2 * Quiet);
        var cell = IntegerCells ? Math.Max(1, Math.Floor(raw)) : raw;
        var origin = IntegerCells ? Math.Floor((Side - cell * n) / 2) : Quiet * cell;
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                if (_modules[y, x])
                {
                    dc.DrawRectangle(Dark, null, new Rect(origin + x * cell, origin + y * cell, cell, cell));
                }
            }
        }
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
