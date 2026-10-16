// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Text.Json;

namespace Kantor.Core.Humor;

/// <summary>Categoría de comentario. El prefijo del ID estable es el nombre de la categoría.</summary>
public enum HumorCategory
{
    Disaster, Trying, Technically, Good, Suspicious, Superstar, Mythical, Divine, Random,
}

/// <summary>Reglas de rango de puntaje (0–10000) → categoría. Sin lógica de selección de frases.</summary>
public static class HumorRanges
{
    public const int PerfectScore = 10000;

    public static HumorCategory CategoryFor(int score) => score switch
    {
        >= 10000 => HumorCategory.Divine,
        >= 9500 => HumorCategory.Mythical,
        >= 9000 => HumorCategory.Superstar,
        >= 8000 => HumorCategory.Suspicious,
        >= 6000 => HumorCategory.Good,
        >= 4000 => HumorCategory.Technically,
        >= 2000 => HumorCategory.Trying,
        _ => HumorCategory.Disaster,
    };

    public static string Prefix(HumorCategory c) => c.ToString().ToLowerInvariant();
}

/// <summary>
/// Bancos de frases por idioma: id estable → texto. Los textos viven en recursos
/// embebidos (humor.{idioma}.json); acá solo se cargan y validan.
/// </summary>
public sealed class HumorBank
{
    public const string DefaultLanguage = "es";
    public static readonly string[] Languages = { "es", "en" };

    private readonly Dictionary<string, string> _texts;
    private readonly Dictionary<HumorCategory, string[]> _ids;

    public string Language { get; }

    public HumorBank(string language, IReadOnlyDictionary<string, string> texts)
    {
        Language = language;
        _texts = new Dictionary<string, string>(texts, StringComparer.Ordinal);
        _ids = new Dictionary<HumorCategory, string[]>();
        foreach (HumorCategory c in Enum.GetValues<HumorCategory>())
        {
            var prefix = HumorRanges.Prefix(c) + "_";
            _ids[c] = _texts.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>IDs de la categoría, en orden estable.</summary>
    public IReadOnlyList<string> Ids(HumorCategory c) => _ids[c];

    public IReadOnlyCollection<string> AllIds => _texts.Keys;

    public bool TryGet(string id, out string text) => _texts.TryGetValue(id, out text!);

    public string Text(string id) => _texts[id];

    public static HumorBank Load(string language)
    {
        var name = $"Kantor.Core.Humor.Texts.humor.{language}.json";
        using var s = typeof(HumorBank).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Falta el recurso de frases '{name}'.");
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(s)
            ?? throw new InvalidOperationException($"Recurso de frases '{name}' vacío.");
        return new HumorBank(language, dict);
    }

    public static string NormalizeLanguage(string? language) =>
        Languages.Contains(language) ? language! : DefaultLanguage;
}
