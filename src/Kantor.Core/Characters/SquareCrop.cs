// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Characters;

/// <summary>
/// Recorte cuadrado sobre una imagen, en píxeles de la imagen. Sirve para sacar la miniatura (la cara) del cuerpo
/// entero. Es geometría pura: nunca sale de la imagen y nunca deja de ser cuadrado.
/// </summary>
public readonly record struct SquareCrop(double X, double Y, double Size)
{
    /// <summary>Lado mínimo del recorte, salvo que la imagen sea más chica.</summary>
    public const double MinSize = 16;

    /// <summary>
    /// Recorte inicial. En una imagen alta (cuerpo entero) apunta a la parte de arriba, donde está la cara;
    /// en una casi cuadrada toma todo el lado menor.
    /// </summary>
    public static SquareCrop Suggested(int width, int height)
    {
        var shortSide = Math.Min(width, height);
        var tall = height > width * 1.25;
        var size = tall ? Math.Min(width, height * 0.35) : shortSide;
        return new SquareCrop((width - size) / 2, tall ? height * 0.02 : (height - size) / 2, size).Clamp(width, height);
    }

    /// <summary>Lleva el recorte adentro de la imagen (tamaño y posición).</summary>
    public SquareCrop Clamp(int width, int height)
    {
        var max = Math.Max(1, Math.Min(width, height));
        var size = Math.Clamp(Size, Math.Min(MinSize, max), max);
        return new SquareCrop(Math.Clamp(X, 0, width - size), Math.Clamp(Y, 0, height - size), size);
    }

    public SquareCrop Moved(double dx, double dy, int width, int height) =>
        new SquareCrop(X + dx, Y + dy, Size).Clamp(width, height);

    /// <summary>Cambia el lado manteniendo el centro (lo que el borde de la imagen permita).</summary>
    public SquareCrop Resized(double newSize, int width, int height)
    {
        var max = Math.Max(1, Math.Min(width, height));
        var size = Math.Clamp(newSize, Math.Min(MinSize, max), max);
        var cx = X + Size / 2;
        var cy = Y + Size / 2;
        return new SquareCrop(cx - size / 2, cy - size / 2, size).Clamp(width, height);
    }

    /// <summary>Rectángulo entero para recortar píxeles (nunca fuera de la imagen, al menos 1×1).</summary>
    public (int X, int Y, int Size) ToPixels(int width, int height)
    {
        var c = Clamp(width, height);
        var size = Math.Max(1, (int)Math.Round(c.Size));
        size = Math.Min(size, Math.Min(width, height));
        var x = Math.Clamp((int)Math.Round(c.X), 0, width - size);
        var y = Math.Clamp((int)Math.Round(c.Y), 0, height - size);
        return (x, y, size);
    }
}
