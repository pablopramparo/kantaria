// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Kantor.Core.Characters;
using Kantor.Core.Guest;

namespace Kantor.App.Guest;

/// <summary>
/// Retratos de los jugadores para el celular: se leen de los recursos de la aplicación o de los archivos del propio
/// catálogo (nunca de una ruta que traiga el cliente), se reducen a 128 px y se entregan como PNG. Puede llamarse
/// desde cualquier hilo.
/// </summary>
internal sealed class GuestAvatarSource : IGuestAvatars
{
    private const int Size = 128;

    private readonly ConcurrentDictionary<string, byte[]?> _cache = new(StringComparer.Ordinal);

    public byte[]? GetPortraitPng(string characterId)
    {
        if (!CharacterCatalog.Default.TryGet(characterId, out var character))
        {
            return null;
        }

        var path = character.ResolvedPortraitPath;
        var stamp = character.IsUser && File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        return _cache.GetOrAdd($"{character.Id}|{stamp}", _ => Render(path));
    }

    private static byte[]? Render(string path)
    {
        try
        {
            Stream? stream = Path.IsPathRooted(path)
                ? File.OpenRead(path)
                : Application.GetResourceStream(new Uri($"pack://application:,,,/{path}", UriKind.Absolute))?.Stream;
            if (stream is null)
            {
                return null;
            }

            using (stream)
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = Size;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = new MemoryStream();
                encoder.Save(output);
                return output.ToArray();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                      or InvalidOperationException or FileFormatException
                                      or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
