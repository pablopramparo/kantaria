// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Reflection;

namespace Kantor.Core.Characters;

/// <summary>
/// Edición de la compilación: <c>Local</c> (instalador y zip: todos los avatares) o <c>Store</c> (Microsoft Store: sin
/// los avatares que no pueden distribuirse por esa vía). Se elige al compilar con <c>-p:Edition=Store</c>, que define
/// <c>STORE_EDITION</c>; <c>scripts/publish.ps1 -Edition Store</c> lo hace. La Store es siempre un subconjunto de la
/// edición local, y la lista de excluidos está en <c>Characters/store-excluded.txt</c> (un id por línea).
/// </summary>
public static class CharacterEdition
{
#if STORE_EDITION
    public static bool IsStore { get; } = true;
#else
    public static bool IsStore { get; } = false;
#endif

    private const string ResourceName = "Kantor.Characters.store-excluded.txt";

    /// <summary>Avatares de fábrica que esta edición no incluye (vacío en la edición local).</summary>
    public static IReadOnlySet<string> Excluded { get; } =
        IsStore ? StoreExcludedList() : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>La lista de excluidos de la Store, tal como está en el recurso (sirve también para validarla).</summary>
    public static IReadOnlySet<string> StoreExcludedList()
    {
        using var stream = typeof(CharacterEdition).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Ids separados por <c>;</c> y/o saltos de línea (MSBuild lee el mismo archivo y separa por <c>;</c>); se ignora lo
    /// vacío.
    /// </summary>
    public static IReadOnlySet<string> Parse(string text) =>
        text.Split([';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
}
