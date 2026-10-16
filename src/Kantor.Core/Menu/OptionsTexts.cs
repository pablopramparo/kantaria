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
        ["tab.help"] = ("GUÍA", "GUIDE"),
        ["tab.credits"] = ("CRÉDITOS", "CREDITS"),

        ["credits.app.title"] = ("Kantor", "Kantor"),
        ["credits.app.body"] = (
            "Karaoke local para Windows, compatible con canciones UltraStar. Creado por Pablo Pramparo. Software libre bajo GPL-3.0-or-later.",
            "Local karaoke for Windows, compatible with UltraStar songs. Created by Pablo Pramparo. Free software under GPL-3.0-or-later."),
        ["credits.inspired.title"] = ("Inspiración", "Inspiration"),
        ["credits.inspired.body"] = (
            "Inspirado en UltraStar Deluxe y UltraStar WorldParty: se tomó su comportamiento como referencia, sin copiar su código.",
            "Inspired by UltraStar Deluxe and UltraStar WorldParty: their behavior was used as a reference, without copying their code."),
        ["credits.art.title"] = ("Arte y marca", "Art and brand"),
        ["credits.art.body"] = (
            "El nombre Kantor, su logo y los personajes son © 2026 Pablo Pramparo, todos los derechos reservados; no están bajo la GPL.",
            "The Kantor name, its logo and the characters are © 2026 Pablo Pramparo, all rights reserved; they are not under the GPL."),
        ["credits.libs.title"] = ("Tecnología de terceros", "Third-party technology"),
        ["credits.libs.body"] = (
            "Jersey 15 (SIL OFL 1.1) · .NET y WPF (MIT) · NAudio (MIT) · LibVLC y LibVLCSharp (LGPL-2.1+) · SQLite y Microsoft.Data.Sqlite (MIT) · ASP.NET Core Kestrel (MIT) · QRCoder (MIT). Los textos completos están en THIRD-PARTY-NOTICES.md.",
            "Jersey 15 (SIL OFL 1.1) · .NET and WPF (MIT) · NAudio (MIT) · LibVLC and LibVLCSharp (LGPL-2.1+) · SQLite and Microsoft.Data.Sqlite (MIT) · ASP.NET Core Kestrel (MIT) · QRCoder (MIT). Full texts are in THIRD-PARTY-NOTICES.md."),
        ["credits.songs.title"] = ("Canciones", "Songs"),
        ["credits.songs.body"] = (
            "Kantor no incluye canciones: las aporta cada usuario y son responsabilidad de quien las posee.",
            "Kantor includes no songs: each user brings their own, and they are the responsibility of whoever owns them."),
        ["credits.version"] = ("Versión {0}", "Version {0}"),
        ["credits.edition.store"] = ("edición Microsoft Store", "Microsoft Store edition"),

        ["help.songs.title"] = ("1. Cargar las canciones", "1. Load your songs"),
        ["help.songs.body"] = (
            "Kantor lee canciones en formato UltraStar: una carpeta por canción, con el archivo .txt, el audio y, si hay, el video y la tapa. En la pestaña BIBLIOTECA tocá «Agregar carpeta…» y elegí la carpeta que las contiene (se buscan también las subcarpetas). Se pueden agregar varias.",
            "Kantor reads UltraStar songs: one folder per song, with the .txt file, the audio and, if present, the video and cover. In the LIBRARY tab choose “Add folder…” and pick the folder that contains them (subfolders are scanned too). You can add several."),
        ["help.mics.title"] = ("2. Configurar los micrófonos", "2. Set up the microphones"),
        ["help.mics.body"] = (
            "En la pestaña AUDIO tocá «Crear micrófono» una vez por cada jugador (hasta 4) y elegí el dispositivo y el canal de cada uno. Hablá o cantá: el medidor y la nota detectada tienen que moverse. Si dice «Sin señal», revisá el canal o subí la ganancia de la interfaz. Con una interfaz de dos canales, cada micrófono va en un canal distinto.",
            "In the AUDIO tab choose “Create microphone” once per player (up to 4) and pick each one’s device and channel. Speak or sing: the meter and the detected note should move. If it says “No signal”, check the channel or raise the interface gain. With a two-channel interface, each microphone goes on a different channel."),
        ["help.latency.title"] = ("3. Retardo de la voz", "3. Voice delay"),
        ["help.latency.body"] = (
            "Con el motor «Windows (compartido)» la voz llega con bastante retardo (cerca de 170 ms con una interfaz USB); con ASIO baja a unos 20 ms. Si cantás bien y el puntaje no lo refleja, probá ASIO en AUDIO o ajustá el «Retardo» de cada micrófono; el botón «Calibrar» lo mide con un ejercicio guiado.",
            "With the “Windows (shared)” engine the voice arrives quite late (about 170 ms with a USB interface); with ASIO it drops to about 20 ms. If you sing well but the score doesn’t show it, try ASIO in AUDIO or adjust each microphone’s “Delay”; the “Calibrate” button measures it with a guided exercise."),
        ["help.play.title"] = ("4. Cantar y armar la partida", "4. Sing and set up a session"),
        ["help.play.body"] = (
            "En el menú elegí CANTAR, buscá la canción y tocá Enter. Con F4 abrís una partida para asignar jugadores a los micrófonos y guardar el historial; con F5 sumás canciones a la cola y con F6 la ves. Los atajos están siempre en la línea de abajo de cada pantalla.",
            "From the menu choose SING, find the song and press Enter. F4 opens a session to assign players to microphones and keep a history; F5 adds songs to the queue and F6 shows it. The shortcuts are always on the bottom line of each screen."),
        ["help.remote.title"] = ("5. Control remoto (opcional)", "5. Remote control (optional)"),
        ["help.remote.body"] = (
            "Si usás Avyn, activá «Control remoto (Avyn)» en la pestaña JUEGO. El canal es local a esta PC y a este usuario, sin red.",
            "If you use Avyn, turn on “Remote control (Avyn)” in the GAME tab. The channel is local to this PC and user, with no network."),

        ["help.guest.title"] = ("6. Invitados con el celular (opcional)", "6. Guests with their phones (optional)"),
        ["help.guest.body"] = (
            "Tocá «INVITADOS · QR» en el menú o en la lista de canciones: aparece un QR. Con la cámara del celular (en la misma red Wi-Fi) los invitados buscan una canción, eligen quién canta (o crean su jugador con nombre y personaje) y la suman a la cola; nada más. Se apaga desde la pantalla del QR o al cerrar Kantor. Es HTTP sin cifrar en tu red local: no lo uses en redes ajenas.",
            "Click “GUESTS · QR” on the menu or in the song list: a QR appears. With their phone camera (on the same Wi-Fi) guests find a song, pick who sings (or create their own player with a name and character) and add it to the queue; nothing else. Turn it off from the QR screen or by closing Kantor. It is plain, unencrypted HTTP on your local network: don’t use it on networks you don’t control."),

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
