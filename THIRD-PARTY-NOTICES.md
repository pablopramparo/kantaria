# Avisos de terceros

Kantor se distribuye bajo GPL-3.0-or-later (ver `LICENSE`). La distribución binaria incluye los siguientes
componentes, cada uno con su propia licencia.

| Componente | Uso | Licencia |
|---|---|---|
| .NET 10 (runtime y WPF) | Plataforma de la aplicación (autocontenida) | MIT |
| [LibVLC](https://www.videolan.org/vlc/libvlc.html) (paquete `VideoLAN.LibVLC.Windows` 3.0.21) | Reproducción de video de fondo | LGPL-2.1-or-later |
| [LibVLCSharp](https://github.com/videolan/libvlcsharp) 3.9.4 | Enlace .NET de LibVLC | LGPL-2.1-or-later |
| [NAudio](https://github.com/naudio/NAudio) 2.2.1 | Captura y reproducción de audio (WASAPI, ASIO) | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) 10.0.13 | Historial de partidas | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) y SQLite | Motor de base de datos | Apache-2.0 / dominio público |
| ASP.NET Core (Kestrel, `Microsoft.AspNetCore.App`) | Servidor HTTP del acceso de invitados | MIT |
| [QRCoder](https://github.com/codebude/QRCoder) 1.6.0 | Matriz del código QR de invitados | MIT |
| [8Bit Title Screen](https://opengameart.org/content/8bit-title-screen) de Joth (JPool), `8bit-title-screen.mp3` | Música de inicio y navegación | CC0 1.0 (sin modificar) |
| [Heroic Loop](https://opengameart.org/content/heroic-loop) de Zane Little Music, `heroic-loop.wav` | Música de resultados | CC0 1.0 (convertido de PCM 32 bits flotante a 16 bits) |
| [Jersey 15](https://fonts.google.com/specimen/Jersey+15) | Tipografía pixelada | SIL OFL 1.1 (`Assets/Fonts/OFL.txt`) |

## Recursos propios fuera de la GPL

El nombre Kantor, su logo y los personajes son del autor y no están bajo la GPL: ver
`src/Kantor.App/Assets/LICENSE-ASSETS.md` (en la distribución, `LICENSE-ASSETS.md`).

## LibVLC (LGPL)

LibVLC se usa como biblioteca dinámica, sin modificar, en la carpeta `libvlc\win-x64`. El código fuente de
VLC está disponible en <https://code.videolan.org/videolan/vlc>. Se puede reemplazar por otra compilación
compatible de LibVLC 3.x copiándola en esa carpeta.

## Canciones

Kantor no incluye canciones. Las canciones UltraStar las aporta el usuario y son responsabilidad de quien las
posee.
