// SPDX-License-Identifier: GPL-3.0-or-later
using Kantor.Core.Humor;

namespace Kantor.Core.Menu;

/// <summary>Textos fijos del menú principal (es/en).</summary>
public static class MenuTexts
{
    public static string Sing(string language) => Pick(language, "CANTAR", "SING");
    public static string Players(string language) => Pick(language, "JUGADORES", "PLAYERS");
    public static string Options(string language) => Pick(language, "OPCIONES", "OPTIONS");
    public static string Exit(string language) => Pick(language, "SALIR", "EXIT");
    public static string Loading(string language) => Pick(language, "CARGANDO…", "LOADING…");
    public static string Pause(string language) => Pick(language, "PAUSA", "PAUSE");
    public static string Resume(string language) => Pick(language, "SEGUIR", "RESUME");
    public static string Finish(string language) => Pick(language, "TERMINAR", "FINISH");
    public static string FinishTitle(string language) => Pick(language, "¿TERMINAR LA CANCIÓN?", "END THE SONG?");
    public static string FinishInfo(string language, bool canSave) => canSave
        ? Pick(language, "Todavía queda letra por cantar. ¿Querés guardar los resultados en la partida?",
            "There are still lyrics left to sing. Do you want to save the results in the session?")
        : Pick(language, "Todavía queda letra por cantar. Sin partida abierta el resultado no se guarda.",
            "There are still lyrics left to sing. With no open session the result is not saved.");
    public static string FinishSave(string language) => Pick(language, "GUARDAR Y VER RESULTADOS", "SAVE AND SEE RESULTS");
    public static string FinishDiscard(string language) => Pick(language, "VER SIN GUARDAR", "SEE WITHOUT SAVING");
    public static string FinishView(string language) => Pick(language, "VER RESULTADOS", "SEE RESULTS");
    public static string FinishContinue(string language) => Pick(language, "SEGUIR CANTANDO", "KEEP SINGING");
    public static string NotSaved(string language) =>
        Pick(language, "Resultado no guardado en la partida.", "Result not saved in the session.");
    public static string BackToMenu(string language) => Pick(language, "MENÚ", "MENU");
    public static string Hint(string language) => Pick(language, "↑ ↓ ELEGIR  ·  ENTER ACEPTAR", "↑ ↓ SELECT  ·  ENTER CONFIRM");

    private static string Pick(string language, string es, string en) =>
        HumorBank.NormalizeLanguage(language) == "en" ? en : es;
}
