// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Humor;

namespace Kantor.Core.Menu;

/// <summary>Textos fijos de la pantalla de opciones (es/en), por clave. Una clave desconocida devuelve la clave.</summary>
public static class OptionsTexts
{
    private static readonly Dictionary<string, (string Es, string En)> Table = new()
    {
        ["title"] = ("OPCIONES", "OPTIONS"),
        ["back"] = ("VOLVER", "BACK"),
        ["tab.audio"] = ("AUDIO", "AUDIO"),
        ["tab.library"] = ("BIBLIOTECA", "LIBRARY"),
        ["tab.game"] = ("JUEGO", "GAME"),
        ["tab.screen"] = ("PANTALLA", "SCREEN"),
        ["hint"] = ("← → PESTAÑAS  ·  ↑ ↓ OPCIONES  ·  ENTER ENTRAR / ABRIR  ·  ESC VOLVER",
            "← → TABS  ·  ↑ ↓ OPTIONS  ·  ENTER ENTER / OPEN  ·  ESC BACK"),

        ["audio.engine"] = ("Motor de audio", "Audio engine"),
        ["audio.engine.windows"] = ("Windows (compartido)", "Windows (shared)"),
        ["audio.engine.asio"] = ("ASIO", "ASIO"),
        ["audio.engine.asio.na"] = ("ASIO (no disponible)", "ASIO (unavailable)"),
        ["audio.driver"] = ("Controlador ASIO", "ASIO driver"),
        ["audio.output"] = ("Salida", "Output"),
        ["audio.mics"] = ("Micrófonos", "Microphones"),
        ["audio.mics.help"] = (
            "Cada micrófono es un dispositivo y uno de sus canales. La cantidad de micrófonos define cuántos jugadores pueden cantar a la vez (hasta 4). El audio no se reproduce ni se guarda.",
            "Each microphone is a device and one of its channels. The number of microphones sets how many players can sing at once (up to 4). Audio is never played back or saved."),
        ["audio.mic"] = ("Micrófono {0}", "Microphone {0}"),
        ["audio.mic.create"] = ("Crear micrófono", "Create microphone"),
        ["audio.mic.remove"] = ("Quitar", "Remove"),
        ["audio.mic.inuse"] = ("Ese canal ya lo usa el micrófono {0}.", "That channel is already used by microphone {0}."),
        ["audio.mic.nochannel"] = ("No quedan canales libres para otro micrófono.", "No free channels left for another microphone."),
        ["audio.nomics"] = ("Todavía no hay micrófonos. Creá uno para poder cantar.",
            "No microphones yet. Create one to be able to sing."),
        ["audio.delay"] = ("Retardo", "Delay"),
        ["audio.calibrate"] = ("Calibrar", "Calibrate"),
        ["audio.nodevices"] = ("No hay dispositivos de entrada activos.", "No active input devices."),
        ["audio.silence"] = ("silencio", "silence"),
        ["audio.nosignal"] = ("Sin señal", "No signal"),
        ["audio.novoice"] = ("Sin voz detectada", "No voice detected"),

        ["lib.folders"] = ("Carpetas de canciones", "Song folders"),
        ["lib.add"] = ("Agregar carpeta…", "Add folder…"),
        ["lib.remove"] = ("Quitar", "Remove"),
        ["lib.none"] = ("Todavía no hay carpetas. Agregá una con las canciones UltraStar.",
            "No folders yet. Add one with your UltraStar songs."),
        ["lib.view"] = ("Vista de la lista", "List view"),
        ["lib.view.list"] = ("Lista", "List"),
        ["lib.view.grid"] = ("Grilla", "Grid"),

        ["game.difficulty"] = ("Dificultad", "Difficulty"),
        ["game.easy"] = ("Fácil", "Easy"),
        ["game.medium"] = ("Medio", "Medium"),
        ["game.hard"] = ("Difícil", "Hard"),
        ["game.voice"] = ("Voz sobre las notas", "Voice over the notes"),
        ["game.voice.beat"] = ("Por beat", "Per beat"),
        ["game.voice.raw"] = ("Cruda", "Raw"),
        ["game.language"] = ("Idioma", "Language"),
        ["game.nonsense"] = ("Comentarios disparatados", "Silly comments"),
        ["game.feedback"] = ("Mensajes por frase al cantar", "Phrase messages while singing"),
        ["game.preview"] = ("Vista previa en la lista", "Preview in the song list"),
        ["game.autoadvance"] = ("Avance automático de la cola", "Queue auto-advance"),
        ["game.autoadvance.help"] = (
            "Al terminar los resultados, 10 s después sigue sola la próxima canción de la cola (solo con partida abierta y jugadores asignados). Cualquier tecla cancela la cuenta.",
            "After the results, the next song in the queue starts by itself 10 s later (only with an open session and assigned players). Any key cancels the countdown."),
        ["game.remote"] = ("Control remoto (Avyn)", "Remote control (Avyn)"),
        ["game.remote.help"] = (
            "Deja que el Agent de Avyn en esta PC controle Kantor por un canal local (sin red, solo este usuario).",
            "Lets the Avyn Agent on this PC control Kantor through a local channel (no network, this user only)."),
        ["game.remote.off"] = ("Apagado", "Off"),
        ["game.remote.waiting"] = ("Esperando al Agent de Avyn.", "Waiting for the Avyn Agent."),
        ["game.remote.connected"] = ("Agent de Avyn conectado.", "Avyn Agent connected."),
        ["game.remote.failed"] = ("No se pudo abrir el canal local.", "Could not open the local channel."),

        ["screen.fullscreen"] = ("Iniciar en pantalla completa", "Start in fullscreen"),
        ["screen.techbar"] = ("Barra técnica", "Technical bar"),
        ["yes"] = ("Sí", "Yes"),
        ["no"] = ("No", "No"),
        ["visible"] = ("Visible", "Visible"),
        ["hidden"] = ("Oculta", "Hidden"),
    };

    /// <summary>Claves definidas (para comprobar que todas tienen texto en ambos idiomas).</summary>
    public static IReadOnlyCollection<string> Keys => Table.Keys;

    public static string Get(string language, string key)
    {
        if (!Table.TryGetValue(key, out var pair))
        {
            return key;
        }

        return HumorBank.NormalizeLanguage(language) == "en" ? pair.En : pair.Es;
    }

    /// <summary>Nombre de un micrófono por su número ("Micrófono 2").</summary>
    public static string Microphone(string language, int number) =>
        string.Format(Get(language, "audio.mic"), number);
}
