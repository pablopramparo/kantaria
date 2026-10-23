// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Humor;

namespace Kantor.Core.Menu;

/// <summary>Textos fijos de jugadores, sesiones e historial (es/en), por clave. Una clave desconocida devuelve la clave.</summary>
public static class PlayersTexts
{
    private static readonly Dictionary<string, (string Es, string En)> Table = new()
    {
        ["title"] = ("JUGADORES", "PLAYERS"),
        ["back"] = ("VOLVER", "BACK"),
        ["tab.players"] = ("JUGADORES", "PLAYERS"),
        ["tab.session"] = ("PARTIDA", "SESSION"),
        ["tab.history"] = ("HISTORIAL", "HISTORY"),
        ["hint"] = ("← → PESTAÑAS  ·  ↑ ↓ OPCIONES  ·  ENTER ENTRAR / ABRIR  ·  ESC VOLVER",
            "← → TABS  ·  ↑ ↓ OPTIONS  ·  ENTER ENTER / OPEN  ·  ESC BACK"),

        ["p.name"] = ("Nombre del jugador", "Player name"),
        ["p.new"] = ("Jugador nuevo: nombre y avatar", "New player: name and avatar"),
        ["p.create"] = ("Crear", "Create"),
        ["p.save"] = ("Guardar", "Save"),
        ["p.cancel"] = ("Cancelar", "Cancel"),
        ["p.rename"] = ("Renombrar", "Rename"),
        ["p.archive"] = ("Archivar", "Archive"),
        ["p.confirm"] = ("¿Seguro?", "Sure?"),
        ["p.none"] = ("Todavía no hay jugadores. Creá el primero.", "No players yet. Create the first one."),
        ["p.help"] = ("Archivar saca al jugador de las listas pero conserva todo su historial.",
            "Archiving removes a player from the lists but keeps their whole history."),
        ["p.editing"] = ("Renombrando a {0}", "Renaming {0}"),

        ["c.title"] = ("Personajes propios", "Your characters"),
        ["c.new"] = ("Cargar personaje", "Add character"),
        ["c.none"] = ("Todavía no cargaste personajes propios.", "You haven't added any characters yet."),
        ["c.help"] = ("Cada personaje tiene un cuerpo entero y una cara cuadrada. Si cargás solo el cuerpo, recortás la cara de ahí.",
            "Each character has a full body and a square face. If you only add the body, you crop the face from it."),
        ["c.newtitle"] = ("Personaje nuevo", "New character"),
        ["c.body"] = ("Cuerpo entero…", "Full body…"),
        ["c.face"] = ("Cara aparte…", "Separate face…"),
        ["c.facefrombody"] = ("Cara del cuerpo entero", "Face from the full body"),
        ["c.body.none"] = ("sin cuerpo entero", "no full body"),
        ["c.crop.hint"] = ("Arrastrá el cuadrado · rueda o + − : tamaño · flechas: mover (Mayús: rápido)",
            "Drag the square · wheel or + − : size · arrows: move (Shift: fast)"),
        ["c.preview"] = ("Cara", "Face"),
        ["c.noimage"] = ("Elegí al menos una imagen.", "Pick at least one image."),
        ["c.failed"] = ("No se pudo usar la imagen: {0}", "The image could not be used: {0}"),
        ["c.saved"] = ("Personaje guardado.", "Character saved."),
        ["c.delete"] = ("Borrar", "Delete"),
        ["c.deleted"] = ("Personaje borrado.", "Character deleted."),

        ["s.open"] = ("Partida abierta desde {0} · {1}", "Session open since {0} · {1}"),
        ["s.none"] = ("No hay partida abierta. Iniciala acá o al entrar a CANTAR.",
            "No open session. Start one here or when you enter SING."),
        ["s.label"] = ("PARTIDA: {0} · {1}", "SESSION: {0} · {1}"),
        ["n.song"] = ("{0} canción", "{0} song"),
        ["n.songs"] = ("{0} canciones", "{0} songs"),
        ["s.label.none"] = ("SIN PARTIDA ABIERTA", "NO OPEN SESSION"),
        ["s.new"] = ("Nueva partida", "Start new session"),
        ["s.close"] = ("Cerrar partida", "Close session"),
        ["s.players"] = ("Jugadores de la partida", "Session players"),
        ["s.noplayers"] = ("Todavía no hay jugadores en la partida.", "No players in the session yet."),
        ["s.add"] = ("Agregar a la partida", "Add to session"),
        ["s.addplayer"] = ("Agregar jugador", "Add player"),
        ["members.add.title"] = ("Agregar jugadores a la partida", "Add players to the session"),
        ["members.add.none"] = ("Ya están todos los jugadores del sistema. Podés crear uno nuevo acá abajo.",
            "All the system's players are in. You can create a new one below."),
        ["s.remove"] = ("Quitar", "Remove"),
        ["s.participants"] = ("Quién canta en cada micrófono", "Who sings on each microphone"),
        ["s.slot"] = ("MIC {0}", "MIC {0}"),
        ["s.voice"] = ("Voz {0}", "Voice {0}"),
        ["s.empty"] = ("—", "—"),
        ["s.persong"] = (
            "Quién canta en cada micrófono se elige al empezar cada canción, entre los jugadores de la partida.",
            "Who sings on each microphone is chosen when each song starts, among the session's players."),
        ["s.ready"] = ("LISTO", "DONE"),
        ["st.title"] = ("Clasificación de la partida", "Session standings"),
        ["st.none"] = ("Todavía no se cantó ninguna canción en esta partida.",
            "No songs have been sung in this session yet."),
        ["st.line"] = ("{0}º   {1}   ·   {2} comp.   ·   {3} % efect.   ·   {4} pts   ·   {5} canc.   ·   {6} gan.",
            "{0}.   {1}   ·   {2} comp.   ·   {3} % eff.   ·   {4} pts   ·   {5} songs   ·   {6} wins"),
        ["st.rules"] = (
            "Competición: en cada canción de 2 o más, el 1º suma tantos puntos como rivales (con 3: 2, 1, 0); el último no suma. " +
            "Efectividad: promedio de puntos sobre 10.000. Pts: puntos musicales totales.",
            "Competition: in each song with 2 or more, 1st gets as many points as rivals (with 3: 2, 1, 0); last gets none. " +
            "Effectiveness: average score out of 10,000. Pts: total music points."),
        ["st.global"] = ("Ranking global", "Global ranking"),
        ["h.f.diff"] = ("Dificultad", "Difficulty"),
        ["h.f.all"] = ("Todas", "All"),
        ["h.f.min"] = ("Mínimo de canciones", "Minimum songs"),
        ["h.exportsession"] = ("Exportar esta partida (CSV)", "Export this session (CSV)"),
        ["h.exportall"] = ("Exportar todo (CSV)", "Export everything (CSV)"),
        ["h.backup"] = ("Respaldar la base de datos", "Back up the database"),
        ["h.delete"] = ("Borrar esta partida", "Delete this session"),
        ["h.deleteopen"] = ("La partida abierta no se borra: cerrala primero.",
            "The open session cannot be deleted: close it first."),
        ["h.saved"] = ("Guardado: {0}", "Saved: {0}"),
        ["h.failed"] = ("No se pudo guardar: {0}", "Could not save: {0}"),
        ["h.deleted"] = ("Partida borrada.", "Session deleted."),
        ["h.restore"] = (
            "Para restaurar un respaldo: con Kantaria cerrado, reemplazá kantor.db (en la carpeta de datos) por la copia.",
            "To restore a backup: with Kantaria closed, replace kantor.db (in the data folder) with the copy."),
        ["h.global"] = ("Ranking global", "Global ranking"),

        ["h.view"] = ("Ver", "View"),
        ["h.bysession"] = ("Por partida", "By session"),
        ["h.byplayer"] = ("Por jugador", "By player"),
        ["h.pick"] = ("Elegir", "Pick"),
        ["h.none"] = ("Todavía no hay canciones terminadas.", "No finished songs yet."),
        ["h.noitems"] = ("No hay nada para mostrar.", "Nothing to show."),
        ["h.open"] = ("abierta", "open"),
        ["h.unnamed"] = ("Partida", "Session"),
        ["h.archived"] = ("archivado", "archived"),
        ["h.easy"] = ("Fácil", "Easy"),
        ["h.medium"] = ("Medio", "Medium"),
        ["h.hard"] = ("Difícil", "Hard"),

        ["s.nomics"] = ("No hay micrófonos habilitados: asignalos en Opciones › Audio.",
            "No microphones enabled: assign them in Options › Audio."),
        ["s.tooptions"] = ("Abrir Opciones › Audio", "Open Options › Audio"),
        ["s.label.free"] = ("SUELTO · el resultado no se guarda", "NO SESSION · result is not saved"),
        ["save.free"] = ("Cantaste suelto: el resultado no se guardó en el historial.",
            "No session: the result was not saved to the history."),
        ["gate.none"] = ("NO HAY PARTIDA ABIERTA", "NO OPEN SESSION"),
        ["gate.start"] = ("NUEVA PARTIDA", "START SESSION"),
        ["gate.free"] = ("CANTAR SUELTO", "SING WITHOUT SESSION"),
        ["gate.hint"] = (
            "Cantando suelto se puede elegir cualquier jugador, pero el resultado no se guarda en el historial.",
            "Without a session you can pick any player, but the result is not saved to the history."),

        ["prompt.title"] = ("HAY UNA PARTIDA ABIERTA", "THERE IS AN OPEN SESSION"),
        ["prompt.info"] = ("Desde {0} · {1}", "Since {0} · {1}"),
        ["prompt.continue"] = ("CONTINUAR PARTIDA", "CONTINUE SESSION"),
        ["prompt.new"] = ("NUEVA PARTIDA", "START NEW"),
        ["prompt.hint"] = ("Una partida nueva cierra la anterior.", "Starting a new one closes the previous."),

        ["members.title"] = ("¿QUIÉN JUEGA?", "WHO IS PLAYING?"),
        ["members.help"] = (
            "Elegí quiénes participan en esta partida. Después, en cada canción, elegís quién canta en cada micrófono.",
            "Pick who takes part in this session. Then, for each song, you choose who sings on each microphone."),
        ["members.need"] = ("Sumá al menos un jugador a la partida.", "Add at least one player to the session."),

        ["pick.choose"] = ("Elegir jugador", "Pick a player"),
        ["pick.who"] = ("¿Quién canta en el MIC {0}?", "Who sings on MIC {0}?"),
        ["pick.nobody"] = ("Nadie", "Nobody"),
        ["pick.back"] = ("Volver", "Back"),
        ["pick.title"] = ("¿QUIÉN CANTA?", "WHO IS SINGING?"),
        ["pick.need"] = ("Elegí quién canta en cada micrófono para esta canción.",
            "Pick who sings on each microphone for this song."),
        ["pick.nomic"] = ("Ningún jugador elegido tiene micrófono asignado. Asignalos en Opciones › Audio.",
            "None of the chosen players has a microphone. Assign them in Options › Audio."),
        ["save.ok"] = ("Resultado guardado en el historial.", "Result saved to the history."),
        ["save.failed"] = ("No se pudo guardar el resultado en el historial: {0}",
            "The result could not be saved to the history: {0}"),
    };

    public static IReadOnlyCollection<string> Keys => Table.Keys;

    public static string Get(string language, string key)
    {
        if (!Table.TryGetValue(key, out var pair))
        {
            return key;
        }

        return HumorBank.NormalizeLanguage(language) == "en" ? pair.En : pair.Es;
    }

    public static string Format(string language, string key, params object[] args) =>
        string.Format(Get(language, key), args);

    /// <summary>"1 canción" / "3 canciones" (o "1 song" / "3 songs").</summary>
    public static string Songs(string language, int count) =>
        Format(language, count == 1 ? "n.song" : "n.songs", count);
}
