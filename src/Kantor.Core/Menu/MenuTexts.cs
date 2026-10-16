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
    public static string BackToMenu(string language) => Pick(language, "MENÚ", "MENU");
    public static string Hint(string language) => Pick(language, "↑ ↓ ELEGIR  ·  ENTER ACEPTAR", "↑ ↓ SELECT  ·  ENTER CONFIRM");

    private static string Pick(string language, string es, string en) =>
        HumorBank.NormalizeLanguage(language) == "en" ? en : es;
}
