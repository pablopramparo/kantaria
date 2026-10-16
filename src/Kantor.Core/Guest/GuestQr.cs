// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using QRCoder;

namespace Kantor.Core.Guest;

/// <summary>Matriz de módulos de un código QR (la interfaz la dibuja a su manera, con píxeles enteros).</summary>
public static class GuestQr
{
    /// <summary>Módulos del QR sin la zona de silencio: <c>true</c> = oscuro. Cuadrada.</summary>
    public static bool[,] Modules(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var quiet = 4; // QRCoder deja la zona de silencio dentro de la matriz
        var size = matrix.Count - 2 * quiet;
        var result = new bool[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                result[y, x] = matrix[y + quiet][x + quiet];
            }
        }

        return result;
    }
}
