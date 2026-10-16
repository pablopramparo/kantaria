// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kantor.Core.Characters;

namespace Kantor.App.Characters;

/// <summary>Lee y prepara las imágenes de los personajes propios: carga, recorte cuadrado y codificación PNG.</summary>
public static class CharacterImages
{
    /// <summary>Lado de la cara guardada (si el recorte es más chico no se agranda).</summary>
    public const int PortraitSize = 256;

    /// <summary>Fondo de los retratos (el mismo que dibuja la interfaz detrás de ellos; el PNG queda transparente).</summary>
    public static readonly Color PortraitBackColor = Color.FromRgb(0x1B, 0x10, 0x40);

    /// <summary>Alto máximo del cuerpo entero guardado: más que eso no se ve en pantalla y solo pesa.</summary>
    public const int FullBodyMaxHeight = 1400;

    /// <summary>Lee cualquier imagen que Windows entienda, como BGRA de 32 bits y congelada.</summary>
    /// <exception cref="IOException">El archivo no se puede leer o no es una imagen.</exception>
    public static BitmapSource Load(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or UriFormatException
                                      or InvalidOperationException or UnauthorizedAccessException
                                      or System.Runtime.InteropServices.COMException)
        {
            throw new IOException(e.Message, e);
        }
    }

    /// <summary>El cuerpo entero como PNG, reducido si es enorme.</summary>
    public static byte[] EncodeFullBody(BitmapSource source)
    {
        var image = source.PixelHeight > FullBodyMaxHeight
            ? Scale(source, (double)FullBodyMaxHeight / source.PixelHeight)
            : source;
        return Png(image);
    }

    /// <summary>La cara: el recorte cuadrado de <paramref name="source"/> a lo sumo de <see cref="PortraitSize"/>.</summary>
    public static byte[] EncodePortrait(BitmapSource source, SquareCrop crop)
    {
        var (x, y, size) = crop.ToPixels(source.PixelWidth, source.PixelHeight);
        BitmapSource face = new CroppedBitmap(source, new Int32Rect(x, y, size, size));
        if (size > PortraitSize)
        {
            face = Scale(face, (double)PortraitSize / size);
        }

        return Png(face);
    }

    /// <summary>La vista previa del recorte (para mostrarla antes de guardar).</summary>
    public static ImageSource Preview(BitmapSource source, SquareCrop crop)
    {
        var (x, y, size) = crop.ToPixels(source.PixelWidth, source.PixelHeight);
        var face = new CroppedBitmap(source, new Int32Rect(x, y, size, size));
        face.Freeze();
        return face;
    }

    private static BitmapSource Scale(BitmapSource source, double factor)
    {
        var scaled = new TransformedBitmap(source, new ScaleTransform(factor, factor));
        scaled.Freeze();
        return scaled;
    }

    private static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
