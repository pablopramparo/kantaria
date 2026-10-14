# Kantor — estado del proyecto

Última actualización: 13 de octubre de 2026 (fin de la sesión del incremento 3).
Resumen técnico del proyecto: estado, decisiones, mediciones, pendientes y trampas conocidas.

## 1. Qué es

Karaoke local para Windows, offline, compatible con canciones UltraStar. Referencia funcional principal:
UltraStar WorldParty; secundaria: USDX. Aplicación de escritorio en C# / .NET 10 / WPF.
Todavía **no puntúa**: hoy abre una canción, la reproduce, muestra notas y letra, captura micrófonos,
detecta el tono y lo ubica en la línea temporal de la canción.

## 2. Acuerdos de trabajo (vigentes)

- Desarrollo incremental sobre la aplicación real; nada de prototipos descartables.
- Sin sobreingeniería: no se agregan capas, abstracciones ni dependencias sin una necesidad concreta.
- **No aplicar compensaciones de latencia** hasta tener evidencia de un desfase real medido.
- **Monitorización de audio apagada** (no hay código de reproducción del micrófono).
- **Mantener YIN** como detector de tono; no copiar el de WorldParty sin evidencia de que mejore.
- Canciones sin `#BPM` se rechazan: no se inventan valores para datos necesarios.
- No agregar perfiles, rankings, sesiones ni cola hasta que se pidan.
- Commits solo cuando el usuario lo pide. Trabajar sobre la base existente.
- Verificar siempre compilación y tests; distinguir lo comprobado de lo que solo compila.

## 3. Estado por incremento

| # | Contenido | Commit |
|---|---|---|
| 1 | Abrir TXT UltraStar, reproducir audio, notas y letra sincronizadas, pantalla completa | `f09a036` |
| 2 | Entradas de audio por canal, nivel en vivo, detección de tono (YIN), ventana "Entradas" | `70d922c` |
| 3 | Tono ubicado en el tiempo de la canción (marcas de tiempo reales), estela en el escenario | `4f36bbf` |

El incremento 3 está completo y verificado por compilación y tests (76 pasan, 1 omitido). Falta la prueba
manual del usuario con voz real (ver §7 y §9).

## 4. Estructura

```
Kantor.sln
src/Kantor.Core   (sin UI ni hardware)
  Songs/    Song, Voice, Line, Note, UltraStarParser, SongMedia
  Timing/   SongTiming (beat <-> tiempo), ISongClock, HostClock + ISongTimeMap, PlaybackClockModel
  Audio/    InputChannelId, PcmDecoder, LevelMeter
  Pitch/    PitchDetector (YIN), PitchTracker, MusicalPitch, PitchTimeline
src/Kantor.App    (WPF)
  Audio/    SongPlayer, WasapiInputCapture, CaptureSession, ActiveInput, AudioInputCatalog
  Stage/    StageControl (notas, letra, estela de tono)
  MainWindow, InputWindow
tests/Kantor.Tests   xUnit; RealLibraryTests es opt-in con KANTOR_SONGS_DIR
```

### Componentes clave

- **`ISongClock`**: posición de la canción; es lo único que consume el escenario (no conoce NAudio).
- **`HostClock`**: base de tiempo común = QPC en unidades de 100 ns. WASAPI informa en esa unidad.
- **`SongPlayer`**: reproduce con `WasapiOut` y lee el par (posición, QPC) del reloj IAudioClock.
  Accede por reflexión al campo privado `audioClient` de `WasapiOut` (NAudio **fijado en 2.2.1**).
  Implementa `ISongClock` e `ISongTimeMap` (`SongPositionAt(hostTicks)`).
- **`PlaybackClockModel`** (Core, testeado): la posición es la lectura cruda de IAudioClock y extrapola una
  lectura a otro instante. **No descuenta tiempo de pausa**: el reloj de WASAPI cuenta solo audio reproducido;
  tras `Pause()` el búfer cargado (~100–140 ms) sigue sonando y recién ahí el reloj se detiene (medido con
  WasapiOut compartido, 100 ms). Descontar ese avance atrasaba la imagen ~100 ms por cada pausa/reanudación.
- **`WasapiInputCapture`**: captura propia (no `WasapiCapture`) porque necesita el QPC de cada paquete.
- **`PitchTracker.Push(muestras, hostInicioPaquete)`**: cada `PitchFrame` lleva `WindowStartHost`/`WindowEndHost`
  (intervalo real de las muestras analizadas); la ventana termina en la última muestra del paquete.
- **`ActiveInput`**: entrada activa de la app; convierte el centro de cada ventana a tiempo de canción
  al llegar y lo guarda en `PitchTimeline`.
- Modelo: `Song.Voices` es una lista sin tope (duetos y más). Un dispositivo ≠ un micrófono:
  `InputChannelId(DeviceId, Channel)` identifica canales independientes.

## 5. Compilar, ejecutar y probar

El SDK **.NET 10** se instaló a nivel usuario en `%USERPROFILE%\.dotnet` (la máquina solo tenía .NET 8
global). No está en el PATH global:

```powershell
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
cd <carpeta del repositorio>
dotnet build Kantor.sln
dotnet test Kantor.sln
# prueba contra la biblioteca real (1000+ canciones):
$env:KANTOR_SONGS_DIR = '<carpeta con canciones>'; dotnet test Kantor.sln
# ejecutar con una canción:
src\Kantor.App\bin\Debug\net10.0-windows\Kantor.App.exe "<carpeta de muestras>\Twenty One Pilots - Saturday\Twenty One Pilots - Saturday.txt"
```

Atajos: Ctrl+O abrir, Espacio play/pausa, F11 pantalla completa, Esc salir, Ctrl+I entradas de audio.

## 6. Lo comprobado

**Con pruebas automáticas (76):** parser (BOM, UTF-8/Windows-1252, coma decimal, duetos, tags desconocidos,
advertencias), conversión beat↔tiempo, resolución de rutas sin escapar de la carpeta, detector YIN con señales
sintéticas (65–880 Hz, armónicos, silencio, ruido), decodificación hasta 8 canales, niveles,
modelo de reloj (pausa/reanudación/extrapolación), marcas de tiempo de ventana, línea temporal.

**Con la biblioteca real (`<carpeta con canciones>`):** 1068 de 1069 canciones se leen y nombran un audio válido.
La única que falla es "Ace of Base – Beautiful Life" (sin `#BPM`).

**Con hardware real (esta máquina):**
- El reloj de salida entrega pares (posición, QPC) coherentes. **Transitorio de arranque**: en los primeros
  ~5 s la posición acumula unos 29 ms de desfase respecto de QPC; después es lineal (pendiente 1,00000,
  residuo ±0,5 ms).
- Marcas de tiempo de captura: paquetes consecutivos continuos a ±1 ms. La webcam tuvo un hueco real de
  ~310 ms (discontinuidad), que el rastreador maneja con `Reset`.
- Retardo de entrega (fin de paquete → callback): ~5 ms típico, hasta ~15 ms. No afecta la exactitud.
- La app real arranca, reproduce, abre la entrada y muestra el estado en la barra sin excepciones.

## 6 bis. Desfase del reloj de salida durante el arranque (documentado, sin compensar)

- **Observación** (13/10/2026, WasapiOut compartido, 100 ms de latencia solicitada, WAV de 44,1 kHz mono,
  salida a 48 kHz): comparando la posición que informa IAudioClock contra QPC, la posición acumula unos
  **29 ms de retraso en los primeros ~5 s** (pendiente 0,988 en 0–2 s, 0,999 en 2–5 s) y después es lineal
  (pendiente 1,00000, residuo ±0,5 ms). Offset medido: −6 ms a los 0,5 s y −35 ms al final de la corrida.
- **Causa: no entendida.** Hipótesis sin verificar: relleno inicial del buffer del motor de audio,
  arranque del dispositivo USB/driver, o el remuestreo 44,1 → 48 kHz de Media Foundation.
- **Efecto sobre la sincronía: no determinado.** El mapeo de captura a canción usa una lectura fresca del par
  (posición, QPC), así que sigue la posición real del dispositivo en cada instante. Lo que falta saber es si
  ese par describe lo que se oye durante el transitorio, y eso solo lo responde una medición acústica.
- **Decisión:** no se aplica ninguna compensación. Para investigarlo, con poco esfuerzo: repetir la medición
  con otro WAV de 48 kHz (descarta remuestreo), con otro dispositivo de salida, y con la salida ya iniciada
  (silencio previo) para ver si desaparece. No es un proyecto aparte: se hace junto con la prueba acústica.

## 7. Pendiente de medir o probar

- **Latencia acústica total** (salida + aire + entrada): **no medida**. Los intentos con ráfagas por altavoz
  no fueron concluyentes (la Focusrite con micrófono no distinguía las ráfagas del ruido; la webcam tampoco).
  Hace falta cable de loopback (salida → entrada de la interfaz) o micrófono junto al altavoz. Hasta
  entonces la sincronía absoluta con lo que se oye no está demostrada.
- **Detección de voz real**: probar cantando en la Focusrite. El micrófono del usuario es modesto y
  tiene graves flojos: puede quedar bajo la puerta de −50 dBFS o fallar por debajo de ~80 Hz.
- Independencia de los dos canales de la Focusrite con dos micrófonos.
- Pausa/reanudación a oído, pantalla completa (F11), fluidez a 60 fps, desconexión del USB en captura.
- Estela de tono en el escenario con voz real (el dibujo no se verificó con señal).
- Interfaces de más de 2 canales: en modo compartido Windows solo expone el formato configurado.
  Puede requerir modo exclusivo o ASIO; se decide con la interfaz concreta.

## 8. Hallazgos de la biblioteca del usuario

- `<carpeta de muestras>` (6 canciones de muestra, mp3 + mp4) y `<carpeta con canciones>` (~1100).
- Audio casi todo `.mp3`; video `.avi` (596), `.mp4` (288), `.flv`, `.mpg`, `.webm`.
- Sin duetos, sin `#RELATIVE`, sin cambios de BPM. 42 con BOM, 513 no UTF-8 (Windows-1252), 1016 con notas
  doradas, 313 con freestyle. Hay `.txt` que no son canciones (readmes) y `__MACOSX`.
- "Sum41 - War": el TXT dice `Sum41 - War.mp3` pero el archivo es `Sum 41 - War.mp3` (no abre; dato roto).
- Carpetas con `[ ]` en el nombre: cuidado con comodines en PowerShell (`-LiteralPath`).

## 9. Siguientes pasos propuestos (no aprobados)

1. ~~Commit del incremento 3~~ (hecho).
2. **El usuario prueba manualmente** (voz real, canales de la Focusrite, estela de tono sobre las notas)
   antes de seguir. No avanzar con puntuación hasta su devolución.
3. La medición de latencia acústica por loopback sigue pendiente; **no debe convertirse en un proyecto
   independiente**: se resuelve con una prueba breve cuando haya cable, junto con el desfase de arranque (§6 bis).
4. Siguiente incremento acordado, tras esas pruebas: **puntuación por beat sobre Kantor**, usando WorldParty
   como referencia y manteniendo el cálculo de puntuación **separado del detector de pitch** (la puntuación
   consume `PitchTimeline`/`PitchSample`; no conoce YIN ni WASAPI). Detalle de la referencia: Referencia verificada en WorldParty (`UNote.pas`): tolerancia 0–2 semitonos según
   dificultad, plegado de octava a ±6, notas rap sin comparar tono, freestyle sin puntuar, 10000 puntos
   (9000 + 1000 de bonus de línea si está activo). Falta ver el cálculo del bonus y el offset de detección.
   Decisión pendiente: asignación micrófono→voz por actuación (hoy la estela se dibuja solo en la voz 0).
4. Biblioteca con carpetas elegidas expresamente (SQLite) y búsqueda.
5. Video (probablemente FFmpeg; hay que hacer el censo de códecs), y más adelante perfiles, sesiones, cola y Avyn.

Decisiones abiertas del documento base: definición de "puntos de competición" y "efectividad", umbral de
paridad con WorldParty, hardware objetivo.

**Licencia (decidida): GPL-3.0-or-later.** Archivo `LICENSE` en la raíz y aviso en `README.md`.
- WorldParty: GPL-3.0-or-later (verificado en `LICENSE` y en la cabecera de `src/base/UMain.pas`).
- USDX: GPL-2.0-or-later (el `LICENSE` es el texto v2, pero las cabeceras de fuente dicen "or any later
  version", p. ej. `src/base/UMain.pas`). Compatible con GPL-3.0-or-later. Verificado en una muestra, no
  archivo por archivo: si algún día se reutiliza un archivo concreto, mirar su cabecera.
- **Hasta hoy no se copió código de WorldParty ni de USDX.** El scoring es implementación propia, tomando
  WorldParty solo como referencia de comportamiento. Reutilizar código concreto exige proponerlo antes y
  verificar la cabecera del archivo (más atribución y conservar sus avisos de copyright).
- Dependencias futuras (FFmpeg, ASIO, códecs): evaluar la licencia cuando se necesiten. Hoy: NAudio (MIT),
  .NET/WPF (MIT); xunit/coverlet solo en tests.
- Sin infraestructura de contribuciones (CLA/DCO) por ahora.

## 10. Trampas conocidas

- **PowerShell 5.1 `Set-Content`/`Get-Content` corrompe UTF-8** (inserta bytes sueltos de Windows-1252). Un solo
  byte inválido hace que el compilador lea todo el archivo como ANSI y el texto salga con "â€”". Editar
  fuentes con las herramientas Edit/Write, no con `Set-Content`. Si pasa, validar con
  `UTF8Encoding(false, true)` y recodificar los bytes inválidos.
- `[IO.File]` en PowerShell resuelve rutas relativas contra el cwd del proceso, no contra `Set-Location`.
- `InputManager` choca con `System.Windows.Input.InputManager`; por eso la clase se llama `ActiveInput`.
- `TimeSpan` redondea a ticks de 100 ns: en tests de ida y vuelta usar tolerancia ~1e-4 beats.
- Un `.wav` de prueba de 5 s con muchas iteraciones de `Sleep(3)` (~15 ms reales) puede terminar antes de lo esperado.
