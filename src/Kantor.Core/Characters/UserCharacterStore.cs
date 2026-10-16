// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Settings;

namespace Kantor.Core.Characters;

/// <summary>
/// Personajes cargados por el usuario, en <c>{datos}\Characters\{id}\</c>: <c>fullbody.png</c> y/o
/// <c>portrait.png</c> (ya recortado, cuadrado). Sin nombre propio. Los PNG llegan ya
/// codificados: acá no hay nada de imágenes, solo archivos. Un personaje a medio guardar o roto se ignora.
/// </summary>
public sealed class UserCharacterStore
{
    public const string IdPrefix = "usuario-";

    private readonly string _root;

    public UserCharacterStore(string root) => _root = root;

    public static string DefaultRoot() => Path.Combine(AppPaths.DataDirectory(), "Characters");

    public static bool IsUserId(string? id) =>
        CharacterDefinition.IsValidId(id) && id!.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>Los personajes guardados, del más viejo al más nuevo. Las rutas son absolutas.</summary>
    public IReadOnlyList<CharacterDefinition> Load()
    {
        var found = new List<(DateTime Created, CharacterDefinition Character)>();
        if (!Directory.Exists(_root))
        {
            return [];
        }

        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var id = Path.GetFileName(dir);
            if (!IsUserId(id))
            {
                continue;
            }

            var portrait = Path.Combine(dir, CharacterResources.PortraitFile);
            var fullBody = Path.Combine(dir, CharacterResources.FullBodyFile);
            var hasPortrait = File.Exists(portrait);
            var hasFullBody = File.Exists(fullBody);
            if (hasPortrait || hasFullBody)
            {
                // sin nombre propio: el nombre interno es el ID y la interfaz no lo muestra
                found.Add((Directory.GetCreationTimeUtc(dir), new CharacterDefinition(id, id,
                    hasPortrait ? portrait : null, hasFullBody ? fullBody : null, IsUser: true)));
            }
        }

        return found.OrderBy(f => f.Created).ThenBy(f => f.Character.Id, StringComparer.Ordinal)
            .Select(f => f.Character).ToList();
    }

    /// <summary>Guarda un personaje nuevo; al menos una de las dos imágenes es obligatoria.</summary>
    /// <exception cref="ArgumentException">Sin ninguna imagen.</exception>
    public CharacterDefinition Add(byte[]? fullBodyPng, byte[]? portraitPng)
    {
        if (fullBodyPng is not { Length: > 0 } && portraitPng is not { Length: > 0 })
        {
            throw new ArgumentException("Falta al menos una imagen.", nameof(fullBodyPng));
        }

        // El ID es aleatorio (no un contador): un ID borrado no vuelve a aparecer con otra imagen.
        var id = IdPrefix + Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(_root, id);
        var temp = dir + ".tmp";
        Directory.CreateDirectory(temp);
        try
        {
            if (fullBodyPng is { Length: > 0 })
            {
                File.WriteAllBytes(Path.Combine(temp, CharacterResources.FullBodyFile), fullBodyPng);
            }

            if (portraitPng is { Length: > 0 })
            {
                File.WriteAllBytes(Path.Combine(temp, CharacterResources.PortraitFile), portraitPng);
            }

            Directory.Move(temp, dir);
        }
        catch
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // se limpia en el próximo intento
            }

            throw;
        }

        return Load().First(c => c.Id == id);
    }

    /// <summary>Borra un personaje propio. Un ID que no es de usuario o no existe no hace nada.</summary>
    public void Delete(string id)
    {
        if (!IsUserId(id))
        {
            return;
        }

        var dir = Path.Combine(_root, id);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
