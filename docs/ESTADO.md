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
| — | Correcciones de las pruebas manuales: desfase acumulativo en pausa; plegado de octava de la estela | `8a34657`, `d6d390f` |
| 4 | Puntuación por beat (una voz, una entrada), selector de dificultad, puntaje visible | sin commit (a probar) |

Las pruebas funcionales de los incrementos 1–3 fueron aprobadas por el usuario. El incremento 4 está
implementado y verificado por compilación y tests; falta su prueba manual (ver §6 ter y §9).

## 4. Estructura

```
Kantor.sln
src/Kantor.Core   (sin UI ni hardware)
  Songs/    Song, Voice, Line, Note, UltraStarParser, SongMedia
  Timing/   SongTiming (beat <-> tiempo), ISongClock, HostClock + ISongTimeMap, PlaybackClockModel
  Audio/    InputChannelId, PcmDecoder, LevelMeter; AudioEngine.cs: IAudioEngine (motor), ISongPlayer,
            ICaptureSession, AudioInputDevice, AudioEngineChoice (motor pedido -> disponible, con respaldo)
  Pitch/    PitchDetector (YIN), PitchTracker, MusicalPitch, PitchTimeline
  Calibration/  señal de barridos, filtro por correlación (FFT), reglas de aceptación del retardo
  Settings/ UserSettings (+ perfil de retardo por dispositivo), UserSettingsStore
  Scoring/  Difficulty, ScoringRules, NoteScoring (regla de acierto), BeatSampler (política temporal),
            VoiceScorer (puntaje por beat y bono de línea); no conoce YIN, NAudio ni WPF
src/Kantor.App    (WPF)
  Audio/    WasapiEngine + AudioEngineFactory (motor Windows compartido), SongPlayer, WasapiInputCapture,
            CaptureSession, ActiveInput, AudioInputCatalog, CalibrationRunner, PitchLog
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

- **Latencia salida → entrada por cable (medida, 2026-10-14)**: Scarlett 4i4, WASAPI compartido, salida
  "Altavoces (Focusrite)" → entrada 2 (Analogue 2), clics de 4 ms: **mediana 173 ms** (172–179, dispersión
  ~7 ms en 11 clics; el primero arrastra un arranque). Mide el tramo "el reloj de reproducción dice que suena"
  → "marca de tiempo de la captura"; no incluye aire ni reacción. **Camino acústico** (mismos clics, parlantes de la
  PC → aire → micrófono en la entrada 1, clics a 0,8): mediana **176 ms** (175–177 en 9 clics; un evento espurio
  a 583 ms descartado), es decir ~2–3 ms más que por cable (el aire a ~1 m). Coincide con el máximo del barrido de puntaje
  sobre la grabación de "The Line" (150–200 ms), así que el retardo de voz es casi todo del equipo. La
  correlación global voz/notas en esa grabación pone la voz **~200 ms después** de las notas (173–176 de
  sistema + ~25 de detección y arranque de voz); los inicios de frase llegan ~178 ms tarde (n = 29). La
  latencia **no depende del búfer de salida de Kantor** (100/50/30 ms → 177/174/174 ms; 20 ms empeora a 185):
  está en el driver/USB de la interfaz en modo compartido; bajarla exigiría modo exclusivo o ASIO. Windows
  informa 0 ms de `StreamLatency` en ambos lados (el driver no la declara). **Por ASIO** ("Focusrite USB ASIO",
  6 entradas / 4 salidas, búfer 508 muestras ≈ 10,6 ms), mismos clics y mismo cable: **19,5 ms** de ida y vuelta
  (6 de 6 clics, dispersión 0; por el aire al micrófono 22,1 ms), contra 173 ms en compartido. WASAPI exclusivo:
  los endpoints WDM solo aceptan 16 bits / 48 kHz estéreo, período de 3–10 ms; su latencia **no se midió**.
  NAudio exige hilo STA para ASIO. Con ASIO las salidas 1/2 de la API son los jacks traseros 1/2 (el cable
  quedó en esas). No se probó la convivencia ASIO + audio de Windows de otras aplicaciones. Lo que **no**
  se sabe es cómo se reparte entre salida y entrada: la parte de salida afecta a la sincronía entre notas
  dibujadas y audio oído, la de entrada al desfase de la voz. Herramienta: scratchpad, no está en el repo; para
  repetirla hay que cablear una salida de línea a la entrada 2 con INST apagado y ganancia mínima.
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

## 6 ter. Puntuación (incremento 4)

Referencia de comportamiento: `docs/PUNTUACION_WORLDPARTY.md` (qué hace WorldParty y en qué difiere Kantor).

- **Máximo 10.000**: 9.000 de notas + 1.000 de bono de línea (activado); sin bono, 10.000 de notas.
  Cada beat acertado vale `MaxNotas / valorTotal × factor` (normal y rap 1, dorada y rap dorada 2, freestyle 0).
- **Acierto**: normal y dorada, voz detectada y clase de altura a ≤ tolerancia (Fácil 2, Medio 1, Difícil 0)
  con equivalencia de octava; rap, solo sonido sobre la compuerta; ambos requieren `LevelDb ≥ −50`
  (provisional, sin equivalencia con el umbral de WorldParty).
- **Política temporal** (`BeatSampler`): cada beat se evalúa una vez, en orden, en el instante de su punto medio
  (+ `AcousticLatency`, 0 por defecto) y cuando la posición lo pasó por `Settling` (100 ms; medido ~47 ms de
  retardo de llegada). Muestra elegida: la ventana de análisis que cubre el instante (extremos incluidos) con
  centro más cercano; empate, la anterior; sin ventana que lo cubra, el beat no suma. No usa la última muestra
  ni depende de los FPS. Una muestra que llega después de evaluado el beat se ignora.
- **Octavas (decidido por el usuario tras la prueba manual)**: equivalencia por clase de nota, como WorldParty;
  cantar una octava distinta puntúa igual. No hay modo estricto. La estela sigue plegada al carril (no indica
  la octava real). Si algún día se quiere distinguir, es un cambio de regla a decidir aparte.
- **Ciclo de vida**: pausa conserva el puntaje (la posición queda quieta y no se llama a `Advance`);
  Detener lo reinicia (`Reset`); canción nueva o dificultad nueva crean otro `VoiceScorer`; un retroceso de
  posición > 250 ms se toma como reinicio; un salto hacia adelante evalúa los beats saltados con el historial
  que haya; al terminar la canción el puntaje queda visible hasta volver a empezar.
- **Interfaz**: selector Fácil/Medio/Difícil (solo con la canción detenida) y puntaje en la barra superior
  (tooltip con notas, doradas, líneas y beats). `DispatcherTimer` de 33 ms en `MainWindow`, independiente del dibujo.
- **Retardo de voz** (`ScoringRules.AcousticLatency`; en la interfaz, selector "Retardo" de 0 a 300 ms en pasos
  de 25, **175 ms por omisión**, solo con la canción detenida): con la grabación real de "The Line" el puntaje
  en Fácil subía de 7660 (0 ms) a 8740 (150 ms) y 8810 (200 ms), y bajaba después; la latencia salida → entrada
  medida por cable fue 173 ms (§7), de ahí el valor inicial. WorldParty compensa ~90 ms + medio beat
  (~105 ms aquí). Depende del equipo y del dispositivo de salida, así que es un ajuste del usuario, no una
  constante del sistema. El valor por omisión en Core sigue siendo 0. Efecto colateral: las barras "Por beat"
  aparecen ese retardo más tarde, porque el beat se evalúa después de mirar la voz desplazada.
- **Ajustes persistentes** (`Kantor.Core.Settings.UserSettings` + `UserSettingsStore`; archivo
  `%APPDATA%\Kantor\settings.json`): dificultad, modo de voz, retardo (0–300 ms en pasos de 25, se corrige a un
  valor válido), y la última entrada abierta con su canal. Se guarda al cambiar un selector o abrir/cerrar la
  entrada. Al arrancar se reabre la entrada guardada si el dispositivo sigue disponible (si no, queda sin entrada
  y lo guardado no se toca). Un archivo ausente o dañado da los valores por omisión, sin error.
  **Perfil por dispositivo**: el retardo se guarda por combinación *salida predeterminada + entrada + canal*
  (`UserSettings.DelayProfiles`); al abrir o cambiar la entrada se aplica el de esa combinación si existe, y
  cualquier cambio manual o calibración lo actualiza. Sin perfil rige el retardo global.
- **Motor de audio** (`Kantor.Core.Audio.IAudioEngine`; etapa 1 del plan ASIO): la aplicación ya no usa
  `SongPlayer`/`CaptureSession` directamente sino `ISongPlayer`/`ICaptureSession` que entrega un motor
  (`WasapiEngine` = Windows compartido, el actual y el **respaldo**). Preferencia **global**
  (`UserSettings.AudioEngine`, por omisión Windows; sin ajuste por canción). Si se pide ASIO y no está
  disponible, se usa Windows y se avisa (tooltip junto a la entrada). Hoy ASIO no está implementado: el
  ajuste existe, pero siempre resuelve a Windows. Etapas siguientes: 2) motor ASIO (reproducción y captura en
  el mismo callback, entrada y salida elegibles), 3) calibración con el reloj de ASIO. La calibración y el
  selector de motor en la interfaz esperan a la etapa 2.
- **Calibración del retardo** (botón "Calibrar…", `Kantor.Core.Calibration` + `CalibrationRunner` +
  `CalibrationWindow`): reproduce 5 barridos (200 Hz → 6 kHz, 0,5 s, 1 s de separación) por la salida
  predeterminada de Windows, los busca en el canal de entrada elegido por correlación cruzada (FFT) y mide
  la latencia con las marcas de tiempo de WASAPI. Pica por pico aplica las mismas reglas que el resto:
  mínimo 3 picos con calidad ≥ 8, desvío mediano ≤ 8 ms, latencia entre 0 y 700 ms; sugiere el paso de 25 ms
  más cercano (0–300). Si no, explica qué cambiar (volumen, auriculares, ruido). Requiere entrada abierta y la
  canción detenida. En la Scarlett 4i4: **cable 177,9 ms (± 2,0), aire 180,9 ms (± 4,7), 5/5 barridos**; por la
  ventana, 178 ms (± 3,4) → 175 ms aplicado. Los barridos dan ~4 ms más que el barrido de clics (umbral vs
  correlación sobre el inicio del barrido). No mide la reacción del cantante. Pendiente: calibrar con el audio
  de ASIO y la sugerencia al terminar cada canción.
- **Puente de huecos** (`ScoringRules.GapBridge`, 150 ms, 0 lo apaga): decidido tras analizar un registro real
  (`KANTOR_PITCH_LOG`, "The Line", 222 s): ~13 % de los cuadros fuertes salían sin tono por consonantes,
  respiración y transiciones (min_cmnd medio 0,43). Si en el instante de un beat hay sonido pero no tono, y hay
  tono a ambos lados a ≤ 150 ms entre sí, se evalúa con el vecino con voz más cercano. No extiende finales de
  nota. Los beats dentro de un hueco esperan hasta `GapBridge + Settling` para ver el tono posterior; los demás
  no. Sobre ese registro: 7330 → 7660 puntos en Fácil. YIN no se tocó. El modo de voz "Cruda" no se puentea.
- **Registro de diagnóstico** (`KANTOR_PITCH_LOG=ruta.csv`): una línea por análisis (tiempo, nivel, tono,
  confianza, mínimo de YIN, descartado, discontinuidades). Apagado por omisión; sirve para analizar sesiones.
- **Notas no cantables**: las freestyle (`F`) no se dibujan en el escenario (no tienen tono ni puntúan); su
  letra se sigue mostrando y resaltando. El rap sí se dibuja (se puntúa por sonido).
- **Visualización de la voz** (selector "Voz", cambiable en cualquier momento; por omisión **Por beat**):
  *Por beat* dibuja como UltraStar a partir de `VoiceScorer.Results` (la misma evaluación del puntaje):
  barras de beats contiguos en el mismo tono (`VoiceBars`), sobre la nota y a altura completa si acierta, al
  tono real (a ±6 de la nota) y al 65 % si falla, solo sobre las notas y recortadas en la línea del "ahora";
  llega con el retraso de asentamiento. *Cruda* es la estela de cada análisis de YIN, útil para depurar el
  micrófono. Ver `docs/PUNTUACION_WORLDPARTY.md` §10.
  Compensación visual: la estela *Cruda* se dibuja el retardo de voz antes (alineada con la nota cantada) y el
  modo *Por beat* suma el resultado provisional (`VoiceScorer.Preview`) de los beats cuya voz ya llegó pero que
  todavía no se evalúan, sin tocar el puntaje. Ganancia real de la vista previa: solo el tramo
  asentamiento − (llegada ~45 ms + margen 20 ms) ≈ 35 ms; los ~175 ms de retardo son físicos y la barra de la
  nota que se canta *ahora* no puede existir antes de que llegue su voz.
- **Limitaciones conocidas**: (1) los cuadros que llegan en los ~100–140 ms posteriores a pausar (el búfer
  que sigue sonando) no se guardan en el historial, así que los beats de ese tramo pueden perderse;
  (2) solo la voz 0 y una entrada (sin duetos ni asignación micrófono→voz); (3) con notas solapadas de distinto
  tono el máximo no es alcanzable (WorldParty puntúa cada nota por separado, igual); (4) la compuerta
  de nivel y la latencia acústica no están calibradas con el micrófono del usuario.

## 9. Siguientes pasos propuestos (no aprobados)

1. ~~Commit del incremento 3~~ y de las dos correcciones (hecho).
2. **El usuario prueba manualmente la puntuación** (cantar con voz real, los tres niveles, pausa, detener,
   fin de canción) antes del commit del incremento 4.
3. ~~Medición de latencia por loopback~~ (hecha: 173 ms, §7). Siguen pendientes: separar la parte de salida de
   la de entrada, el desfase de arranque (§6 bis) y calibrar la compuerta de nivel con el micrófono.
4. Decisión pendiente: asignación micrófono→voz por actuación y duetos (hoy solo la voz 0).
5. Biblioteca con carpetas elegidas expresamente (SQLite) y búsqueda.
6. Video (probablemente FFmpeg; hay que hacer el censo de códecs), y más adelante perfiles, sesiones, cola y Avyn.

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
