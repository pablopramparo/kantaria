// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.IO;
using System.Windows.Media.Imaging;
using Kantor.Core.Remote;

namespace Kantor.App.Remote;

/// <summary>
/// Miniaturas de portada para el control remoto: decodifica a un tamaño chico (sin cargar la imagen completa),
/// recomprime a JPEG y baja la calidad hasta que entre en el límite. Puede llamarse desde cualquier hilo. Guarda
/// las últimas miniaturas pedidas (clave: archivo, fecha y tamaño).
/// </summary>
internal sealed class WpfCoverProvider : ICoverProvider
{
    private const int CacheSize = 64;

    private readonly Dictionary<string, CoverImage> _cache = [];
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    public CoverImage? GetThumbnail(string coverPath, int size)
    {
        try
        {
            var info = new FileInfo(coverPath);
            if (!info.Exists)
            {
                return null;
            }

            var key = $"{coverPath.ToLowerInvariant()}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{size}";
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var hit))
                {
                    return hit;
                }
            }

            var image = Render(coverPath, size);
            if (image is not null)
            {
                lock (_gate)
                {
                    _cache[key] = image;
                    _order.Enqueue(key);
                    while (_order.Count > CacheSize)
                    {
                        _cache.Remove(_order.Dequeue());
                    }
                }
            }

            return image;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                      or InvalidOperationException or System.Runtime.InteropServices.COMException
                                      or FileFormatException)
        {
            return null;
        }
    }

    private static CoverImage? Render(string path, int size)
    {
        int width;
        int height;
        using (var probe = File.OpenRead(path))
        {
            var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None).Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
        }

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        if (width >= height)
        {
            bitmap.DecodePixelWidth = Math.Min(size, width);
        }
        else
        {
            bitmap.DecodePixelHeight = Math.Min(size, height);
        }

        bitmap.EndInit();
        bitmap.Freeze();

        for (var quality = 85; quality >= 35; quality -= 10)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            if (stream.Length <= Protocol.CoverMaxBytes)
            {
                return new CoverImage(stream.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
            }
        }

        return null; // no entra ni con la calidad más baja
    }
}
