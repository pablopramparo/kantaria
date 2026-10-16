// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kantor.Core.Characters;

/// <summary>
/// Definición centralizada de los personajes disponibles: los de fábrica (<see cref="Factory"/>; si falta un PNG la
/// interfaz usa su placeholder) y los que carga el usuario
/// (<see cref="SetUserCharacters"/>), que van después.
/// </summary>
public sealed class CharacterCatalog
{
    /// <summary>
    /// Cuántos avatares de fábrica hay en el repositorio (<c>avatar_001</c> … <c>avatar_NNN</c>): subirlo al sumar PNG.
    /// La edición Store ofrece menos (ver <see cref="CharacterEdition"/>): usar <see cref="Factory"/>.Count.
    /// </summary>
    public const int FactoryCount = 30;

    /// <summary>
    /// Los avatares de fábrica, en orden. No tienen nombre propio (<c>Name</c> repite el ID y la interfaz no lo
    /// muestra); sus PNG están en <c>Assets/Characters/{id}/</c>.
    /// </summary>
    public static readonly IReadOnlyList<CharacterDefinition> Factory = BuildFactory(CharacterEdition.Excluded);

    /// <summary>
    /// Los avatares de fábrica sin los excluidos (se conserva el orden). Si se excluyeran todos queda el primero: el
    /// catálogo necesita al menos un personaje.
    /// </summary>
    public static IReadOnlyList<CharacterDefinition> BuildFactory(IReadOnlySet<string> excluded)
    {
        var ids = Enumerable.Range(1, FactoryCount).Select(i => $"avatar_{i:000}").ToList();
        var kept = ids.Where(id => !excluded.Contains(id)).ToList();
        return [.. (kept.Count > 0 ? kept : [ids[0]]).Select(id => new CharacterDefinition(id, id))];
    }

    /// <summary>Personaje usado cuando no hay otro (o cuando el ID pedido no existe): el primero de fábrica.</summary>
    public static readonly CharacterDefinition Fallback = Factory[0];

    /// <summary>Catálogo de la aplicación.</summary>
    public static CharacterCatalog Default { get; } = new(Factory);

    private sealed record State(IReadOnlyList<CharacterDefinition> All, Dictionary<string, CharacterDefinition> ById);

    private volatile State _state;

    public IReadOnlyList<CharacterDefinition> All => _state.All;

    /// <exception cref="ArgumentException">ID inválido o repetido, o catálogo sin personajes.</exception>
    public CharacterCatalog(IEnumerable<CharacterDefinition> characters) => _state = Build(characters.ToList());

    private static State Build(List<CharacterDefinition> all)
    {
        if (all.Count == 0)
        {
            throw new ArgumentException("El catálogo necesita al menos un personaje.", nameof(all));
        }

        var byId = new Dictionary<string, CharacterDefinition>(StringComparer.Ordinal);
        foreach (var character in all)
        {
            if (!CharacterDefinition.IsValidId(character.Id))
            {
                throw new ArgumentException($"ID de personaje inválido: '{character.Id}'.", nameof(all));
            }

            if (string.IsNullOrWhiteSpace(character.Name))
            {
                throw new ArgumentException($"El personaje '{character.Id}' no tiene nombre.", nameof(all));
            }

            if (!byId.TryAdd(character.Id, character))
            {
                throw new ArgumentException($"ID de personaje repetido: '{character.Id}'.", nameof(all));
            }
        }

        return new State(all, byId);
    }

    /// <summary>
    /// Reemplaza los personajes del usuario (los de fábrica no se tocan). Un ID repetido de los de fábrica se
    /// ignora. Los jugadores que usaban uno que ya no está caen al primero al mostrarse.
    /// </summary>
    public void SetUserCharacters(IEnumerable<CharacterDefinition> users)
    {
        var all = _state.All.Where(c => !c.IsUser).ToList();
        var known = all.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var user in users)
        {
            if (known.Add(user.Id))
            {
                all.Add(user with { IsUser = true });
            }
        }

        _state = Build(all);
    }

    public bool TryGet(string? id, out CharacterDefinition character)
    {
        if (id is not null && _state.ById.TryGetValue(id, out var found))
        {
            character = found;
            return true;
        }

        character = null!;
        return false;
    }

    /// <summary>El personaje pedido o, si el ID es desconocido (por ejemplo, un ajuste guardado de otra versión), el primero.</summary>
    public CharacterDefinition GetOrDefault(string? id) => TryGet(id, out var found) ? found : _state.All[0];
}
