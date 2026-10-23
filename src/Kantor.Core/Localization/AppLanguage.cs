// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Globalization;

namespace Kantor.Core.Localization;

/// <summary>
/// Los idiomas de la aplicación: español neutro (<c>es</c>, tuteo), español argentino (<c>es-AR</c>, voseo) e inglés
/// (<c>en</c>). Un solo valor decide la interfaz, los comentarios del resultado y las frases por frase.
/// </summary>
public static class AppLanguage
{
    public const string Spanish = "es";
    public const string ArgentineSpanish = "es-AR";
    public const string English = "en";

    public static readonly IReadOnlyList<string> All = [Spanish, ArgentineSpanish, English];

    /// <summary>
    /// Lleva cualquier etiqueta de idioma a una de las tres: <c>es-AR</c> (con cualquier mayúscula o guion bajo) al
    /// argentino, cualquier otro <c>es…</c> al neutro, <c>en…</c> al inglés; lo desconocido o vacío, al español neutro.
    /// </summary>
    public static string Normalize(string? language)
    {
        var tag = (language ?? string.Empty).Trim().Replace('_', '-');
        if (tag.Equals(ArgentineSpanish, StringComparison.OrdinalIgnoreCase))
        {
            return ArgentineSpanish;
        }

        return tag.Split('-')[0].Equals("en", StringComparison.OrdinalIgnoreCase) ? English : Spanish;
    }

    public static bool IsEnglish(string? language) => Normalize(language) == English;

    /// <summary>El idioma sin variante ("es" o "en"), para quien solo distingue español de inglés.</summary>
    public static string Base(string? language) => IsEnglish(language) ? English : Spanish;

    /// <summary>
    /// El que corresponde a Windows para quien todavía no eligió: argentino con región Argentina, neutro con cualquier
    /// otro español e inglés con el resto.
    /// </summary>
    public static string FromSystem()
    {
        try
        {
            var culture = CultureInfo.CurrentUICulture;
            if (culture.TwoLetterISOLanguageName.Equals("es", StringComparison.OrdinalIgnoreCase))
            {
                return RegionIsArgentina() ? ArgentineSpanish : Spanish;
            }

            return English;
        }
        catch (CultureNotFoundException)
        {
            return Spanish;
        }
    }

    private static bool RegionIsArgentina()
    {
        try
        {
            return RegionInfo.CurrentRegion.TwoLetterISORegionName.Equals("AR", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
