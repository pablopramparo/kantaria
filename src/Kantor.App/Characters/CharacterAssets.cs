// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kantor.App.Results;
using Kantor.Core.Characters;

namespace Kantor.App.Characters;

/// <summary>Un personaje con sus imágenes ya resueltas; <c>null</c> en una imagen significa "usar el placeholder".</summary>
public sealed record ResolvedCharacter(CharacterDefinition Definition, ImageSource? Portrait, ImageSource? FullBody)
{
    /// <summary>
    /// Completa el resultado de un jugador con el cuerpo entero y los ajustes del personaje. No toca el nombre
    /// del jugador, su puntaje ni su comentario.
    /// </summary>
    public PlayerResult ApplyTo(PlayerResult result) =>
        result with { Avatar = FullBody, Tuning = Definition.Tuning };
}

/// <summary>
/// Resuelve los recursos de un personaje desde los recursos embebidos de la aplicación
/// (<c>Assets/Characters/{id}/portrait.png</c> y <c>fullbody.png</c>, como <c>Resource</c>) o, en los personajes
/// del usuario, desde sus archivos. Los PNG se
/// cargan una sola vez; si falta alguno no es un error: queda <c>null</c> y la interfaz usa su placeholder.
/// </summary>
public sealed class CharacterAssets
{
    private readonly CharacterCatalog _catalog;
    private readonly Func<string, ImageSource?> _load;
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.Ordinal);

    public CharacterAssets(CharacterCatalog? catalog = null, Func<string, ImageSource?>? loader = null)
    {
        _catalog = catalog ?? CharacterCatalog.Default;
        _load = loader ?? LoadEmbedded;
    }

    public ResolvedCharacter Resolve(string? characterId)
    {
        var definition = _catalog.GetOrDefault(characterId);
        return new ResolvedCharacter(definition, Image(definition.ResolvedPortraitPath), Image(definition.ResolvedFullBodyPath));
    }

    private ImageSource? Image(string path)
    {
        if (!_cache.TryGetValue(path, out var image))
        {
            _cache[path] = image = _load(path);
        }

        return image;
    }

    private static ImageSource? LoadEmbedded(string path)
    {
        try
        {
            // Los personajes del usuario son archivos absolutos; los de fábrica, recursos embebidos.
            var uri = Path.IsPathRooted(path)
                ? new Uri(path, UriKind.Absolute)
                : new Uri($"pack://application:,,,/{path}", UriKind.Absolute);
            var frame = BitmapFrame.Create(uri, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception e) when (e is IOException or UriFormatException or NotSupportedException
                                      or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null; // el recurso aún no existe: se usa el placeholder
        }
    }
}
