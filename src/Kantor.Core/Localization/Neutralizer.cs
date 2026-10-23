// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Kantor.Core.Localization;

/// <summary>
/// Pasa un texto escrito en voseo (español argentino) a tuteo (español neutro). Los textos de la aplicación se
/// escriben una sola vez, en voseo; el neutro sale de acá. Es una lista cerrada de formas verbales y de reglas para
/// "vos": no entiende español, y por eso un test revisa todos los textos buscando voseo que haya quedado.
/// </summary>
public static partial class Neutralizer
{
    /// <summary>Formas de voseo (en minúscula) y su equivalente con tú. Incluye cambios de raíz (elegí → elige, volvé → vuelve).</summary>
    private static readonly Dictionary<string, string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        // imperativos
        ["abrí"] = "abre", ["aceptá"] = "acepta", ["acercá"] = "acerca", ["activá"] = "activa", ["actualizá"] = "actualiza",
        ["actuá"] = "actúa", ["agregá"] = "agrega", ["ajustá"] = "ajusta", ["arrastrá"] = "arrastra", ["bajá"] = "baja",
        ["buscá"] = "busca", ["buscalo"] = "búscalo", ["cantá"] = "canta", ["conectá"] = "conecta", ["confirmá"] = "confirma",
        ["creá"] = "crea", ["declará"] = "declara", ["dejá"] = "deja", ["detené"] = "detén", ["devolvé"] = "devuelve",
        ["elegí"] = "elige", ["escaneá"] = "escanea", ["escribí"] = "escribe", ["esperá"] = "espera", ["evitá"] = "evita",
        ["firmá"] = "firma", ["hablá"] = "habla", ["hacé"] = "haz", ["ignorá"] = "ignora", ["mantené"] = "mantén",
        ["mirá"] = "mira", ["pagá"] = "paga", ["probá"] = "prueba", ["recortá"] = "recorta", ["reemplazá"] = "reemplaza",
        ["repetí"] = "repite", ["revisá"] = "revisa", ["seguí"] = "sigue", ["subí"] = "sube", ["sumá"] = "suma",
        ["tocá"] = "toca", ["tomá"] = "toma", ["tratá"] = "trata", ["usá"] = "usa", ["volvé"] = "vuelve", ["cambiá"] = "cambia",
        ["cerrá"] = "cierra", ["cargá"] = "carga", ["pasá"] = "pasa", ["poné"] = "pon", ["tené"] = "ten", ["vení"] = "ven",
        ["andá"] = "ve", ["decí"] = "di", ["salí"] = "sal", ["fijá"] = "fija", ["apagá"] = "apaga", ["empezá"] = "empieza",
        ["descargá"] = "descarga", ["ingresá"] = "ingresa", ["seleccioná"] = "selecciona", ["apretá"] = "aprieta",
        ["pedí"] = "pide", ["escribilo"] = "escríbelo", ["acordate"] = "acuérdate", ["sumate"] = "súmate", ["mirate"] = "mírate",
        // presente de voseo
        ["abrís"] = "abres", ["aceptás"] = "aceptas", ["acertás"] = "aciertas", ["cambiás"] = "cambias", ["cantás"] = "cantas",
        ["cargás"] = "cargas", ["cerrás"] = "cierras", ["debés"] = "debes", ["dedicás"] = "dedicas", ["dominás"] = "dominas",
        ["elegís"] = "eliges", ["escondés"] = "escondes", ["hacés"] = "haces", ["podés"] = "puedes", ["preferís"] = "prefieres",
        ["querés"] = "quieres", ["recortás"] = "recortas", ["sabés"] = "sabes", ["seguís"] = "sigues", ["sonás"] = "suenas",
        ["sumás"] = "sumas", ["tenés"] = "tienes", ["tocás"] = "tocas", ["usás"] = "usas", ["venís"] = "vienes", ["decís"] = "dices",
        ["ponés"] = "pones", ["pedís"] = "pides", ["sentís"] = "sientes", ["vivís"] = "vives", ["escribís"] = "escribes",
        ["sos"] = "eres",
    };

    // "con vos" → "contigo"; "entre/según/como… vos" → "tú"; "a/de/en/por… vos" → "ti"; "vos" suelto → "tú"
    [GeneratedRegex(@"\b(con) vos\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConVos();

    [GeneratedRegex(@"\b(entre|según|excepto|salvo|menos|incluso|hasta|como) vos\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TuAfterPreposition();

    [GeneratedRegex(@"\b(a|de|en|por|para|sin|sobre|hacia|desde|ante|tras|contra|bajo) vos\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TiAfterPreposition();

    [GeneratedRegex(@"\bvos\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Vos();

    [GeneratedRegex(@"\p{L}+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    /// <summary>Las formas que conoce el conversor (para los tests).</summary>
    public static IReadOnlyCollection<string> KnownForms => Verbs.Keys;

    public static string ToNeutral(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return Cache.GetOrAdd(text, Convert);
    }

    private static string Convert(string text)
    {
        var s = ConVos().Replace(text, m => MatchCase(m.Value, "contigo"));
        s = TuAfterPreposition().Replace(s, m => m.Groups[1].Value + " " + MatchCase(m.Value[(m.Groups[1].Length + 1)..], "tú"));
        s = TiAfterPreposition().Replace(s, m => m.Groups[1].Value + " " + MatchCase(m.Value[(m.Groups[1].Length + 1)..], "ti"));
        s = Vos().Replace(s, m => MatchCase(m.Value, "tú"));
        return Word().Replace(s, m => Verbs.TryGetValue(m.Value, out var neutral) ? MatchCase(m.Value, neutral) : m.Value);
    }

    /// <summary>Copia las mayúsculas de lo que había: TODO EN MAYÚSCULAS, Inicial o minúsculas.</summary>
    private static string MatchCase(string original, string replacement)
    {
        if (original.Length > 1 && original.All(c => !char.IsLetter(c) || char.IsUpper(c)))
        {
            return replacement.ToUpperInvariant();
        }

        if (char.IsUpper(original[0]))
        {
            var sb = new StringBuilder(replacement);
            sb[0] = char.ToUpperInvariant(sb[0]);
            return sb.ToString();
        }

        return replacement;
    }
}
