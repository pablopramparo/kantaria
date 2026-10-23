// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Localization;

/// <summary>Elige el texto de una pantalla según el idioma. El español se escribe en voseo; el neutro se deriva.</summary>
public static class Localize
{
    /// <param name="language">"es", "es-AR" o "en" (cualquier otro valor cae al español neutro).</param>
    /// <param name="spanish">El texto en español argentino (voseo).</param>
    /// <param name="english">El texto en inglés.</param>
    public static string Pick(string? language, string spanish, string english) => AppLanguage.Normalize(language) switch
    {
        AppLanguage.English => english,
        AppLanguage.ArgentineSpanish => spanish,
        _ => Neutralizer.ToNeutral(spanish),
    };

    /// <summary>Solo el español: argentino tal cual, neutro derivado.</summary>
    public static string Spanish(string? language, string spanish) =>
        AppLanguage.Normalize(language) == AppLanguage.ArgentineSpanish ? spanish : Neutralizer.ToNeutral(spanish);
}
