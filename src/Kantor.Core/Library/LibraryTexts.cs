// SPDX-License-Identifier: GPL-3.0-or-later
using Kantor.Core.Humor;
using Kantor.Core.Localization;

namespace Kantor.Core.Library;

/// <summary>Textos fijos de la pantalla de la biblioteca (es/en).</summary>
public static class LibraryTexts
{
    public static string Title(string language) => Pick(language, "ELEGÍ UNA CANCIÓN", "PICK A SONG");
    public static string SearchPlaceholder(string language) =>
        Pick(language, "Buscar por artista, título, género, año…", "Search by artist, title, genre, year…");
    public static string Count(string language, int shown, int total) =>
        shown == total ? Pick(language, $"{total:N0} canciones", $"{total:N0} songs")
                       : Pick(language, $"{shown:N0} de {total:N0}", $"{shown:N0} of {total:N0}");
    public static string Scanning(string language, int found) =>
        Pick(language, $"Buscando canciones… {found:N0}", $"Scanning songs… {found:N0}");
    public static string NoResults(string language) => Pick(language, "Sin resultados", "No results");
    public static string EmptyLibrary(string language) =>
        Pick(language, "Todavía no hay canciones", "No songs yet");
    public static string Hint(string language) => Pick(language,
        "↑ ↓ ← → ELEGIR  ·  ENTER CANTAR  ·  F4 PARTIDA  ·  F5 + COLA  ·  F6 COLA  ·  F3 VISTA  ·  F2 CARPETAS  ·  ESC VOLVER",
        "↑ ↓ ← → SELECT  ·  ENTER SING  ·  F4 SESSION  ·  F5 + QUEUE  ·  F6 QUEUE  ·  F3 VIEW  ·  F2 FOLDERS  ·  ESC BACK");
    public static string Session(string language) => Pick(language, "PARTIDA", "SESSION");
    public static string Back(string language) => Pick(language, "VOLVER", "BACK");
    public static string Video(string language) => Pick(language, "CON VIDEO", "VIDEO");
    public static string Duet(string language) => Pick(language, "DÚO", "DUET");
    public static string ListView(string language) => Pick(language, "LISTA", "LIST");
    public static string ChooseFolder(string language) => Pick(language, "ELEGIR CARPETA", "CHOOSE FOLDER");
    public static string GridView(string language) => Pick(language, "GRILLA", "GRID");

    private static string Pick(string language, string es, string en) => Localize.Pick(language, es, en);
}
