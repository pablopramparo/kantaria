// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kantor.App.Library;

/// <summary>
/// Carga carátulas a pedido (solo las filas visibles) con un tamaño de decodificación chico y una caché acotada.
/// Un archivo que no se puede leer devuelve <c>null</c>: la fila muestra su recuadro vacío.
/// </summary>
public sealed class CoverConverter : IValueConverter
{
    private const int MaxCached = 160;
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public int DecodeWidth { get; set; } = 128;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0)
        {
            return null;
        }

        var key = $"{path}|{DecodeWidth}";
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ImageSource? image = null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.DecodePixelWidth = DecodeWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException
                                      or InvalidOperationException or UriFormatException or FileFormatException)
        {
            // carátula ilegible: sin imagen
        }

        if (Cache.Count >= MaxCached)
        {
            Cache.Clear();
        }

        Cache[key] = image;
        return image;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
