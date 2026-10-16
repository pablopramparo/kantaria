// SPDX-License-Identifier: GPL-3.0-or-later
using Kantor.Core.Humor;

namespace Kantor.Core.Results;

/// <summary>
/// Clasificación visual del puntaje (sello de la pantalla de resultados). Es independiente del comentario
/// humorístico: coinciden hoy en los límites de rango, pero cada uno puede cambiar sin tocar al otro.
/// </summary>
public enum ResultRank
{
    SystemSurvived, NiceTry, Acceptable, NiceWork, Impressive, Superstar, Legendary, Perfect,
}

public static class ResultRanks
{
    public static ResultRank For(int score) => score switch
    {
        >= 10000 => ResultRank.Perfect,
        >= 9500 => ResultRank.Legendary,
        >= 9000 => ResultRank.Superstar,
        >= 8000 => ResultRank.Impressive,
        >= 6000 => ResultRank.NiceWork,
        >= 4000 => ResultRank.Acceptable,
        >= 2000 => ResultRank.NiceTry,
        _ => ResultRank.SystemSurvived,
    };

    /// <summary>Texto del sello en el idioma pedido ("es" o "en"; cualquier otro cae al predeterminado).</summary>
    public static string Label(ResultRank rank, string language) =>
        HumorBank.NormalizeLanguage(language) == "en" ? English[rank] : Spanish[rank];

    private static readonly Dictionary<ResultRank, string> English = new()
    {
        [ResultRank.SystemSurvived] = "SYSTEM SURVIVED",
        [ResultRank.NiceTry] = "NICE TRY!",
        [ResultRank.Acceptable] = "ACCEPTABLE",
        [ResultRank.NiceWork] = "NICE WORK!",
        [ResultRank.Impressive] = "IMPRESSIVE!",
        [ResultRank.Superstar] = "SUPERSTAR!",
        [ResultRank.Legendary] = "LEGENDARY!",
        [ResultRank.Perfect] = "PERFECT!",
    };

    private static readonly Dictionary<ResultRank, string> Spanish = new()
    {
        [ResultRank.SystemSurvived] = "SISTEMA INTACTO",
        [ResultRank.NiceTry] = "¡LO INTENTASTE!",
        [ResultRank.Acceptable] = "APROBADO... CREO",
        [ResultRank.NiceWork] = "¡NADA MAL!",
        [ResultRank.Impressive] = "¡IMPRESIONANTE!",
        [ResultRank.Superstar] = "¡SUPERSTAR!",
        [ResultRank.Legendary] = "¡LEGENDARIO!",
        [ResultRank.Perfect] = "¡PERFECTO!",
    };
}

/// <summary>Textos fijos de la pantalla de resultados (no son frases de humor).</summary>
public static class ResultTexts
{
    public static string Title(string language) => Pick(language, "RESULTADO", "RESULTS");
    public static string Player(string language, int number) => Pick(language, $"JUGADOR {number}", $"PLAYER {number}");
    public static string Winner(string language) => Pick(language, "¡GANADOR!", "WINNER!");
    public static string Tie(string language) => Pick(language, "¡EMPATE!", "TIE!");
    public static string Points(string language) => Pick(language, "PUNTOS", "POINTS");
    public static string Replay(string language) => Pick(language, "OTRA VEZ", "PLAY AGAIN");
    public static string Close(string language) => Pick(language, "CERRAR", "CLOSE");
    public static string Standings(string language) => Pick(language, "CLASIFICACIÓN", "STANDINGS");
    public static string Accuracy(string language) => Pick(language, "PRECISIÓN", "ACCURACY");

    private static string Pick(string language, string es, string en) =>
        HumorBank.NormalizeLanguage(language) == "en" ? en : es;
}
