# Kantor — estado del proyecto

Última actualización: 16 de octubre de 2026 (acceso de invitados por LAN; ver §8 duodecies).
Resumen técnico del proyecto: estado, decisiones, mediciones, pendientes y trampas conocidas.

## 1. Qué es

Karaoke local para Windows, offline, compatible con canciones UltraStar. Referencia funcional principal:
UltraStar WorldParty; secundaria: USDX. Aplicación de escritorio en C# / .NET 10 / WPF.
Abre una canción, la reproduce con su video de fondo, muestra notas y letra, captura hasta cuatro micrófonos,
detecta el tono, puntúa por beat y guarda jugadores, partidas e historial (ver los incrementos de §3 y §8).

## 2. Principios del proyecto

- Desarrollo incremental sobre la aplicación real.
- Sin sobreingeniería: no se agregan capas, abstracciones ni dependencias sin una necesidad concreta.
- **Sin compensaciones de latencia** hasta tener evidencia de un desfase real medido.
- **Monitorización de audio apagada** (no hay código de reproducción del micrófono).
- **YIN** como detector de tono; no se usa el de WorldParty sin evidencia de que mejore.
- Canciones sin `#BPM` se rechazan: no se inventan valores para datos necesarios.
- Licencia **GPL-3.0-or-later**; toda dependencia nueva debe ser compatible.
- Se distingue siempre lo comprobado con tests o hardware de lo que solo compila.

## 3. Estado por incremento

| # | Contenido | Commit |
|---|---|---|
| 1 | Abrir TXT UltraStar, reproducir audio, notas y letra sincronizadas, pantalla completa | `d0537c5` |
| 2 | Entradas de audio por canal, nivel en vivo, detección de tono (YIN), ventana "Entradas" | `8c22edf` |
| 3 | Tono ubicado en el tiempo de la canción (marcas de tiempo reales), estela en el escenario | `b701a35` |
| — | Correcciones de las pruebas manuales: desfase acumulativo en pausa; plegado de octava de la estela | `390c893`, `2932c9f` |
| 4 | Puntuación por beat (una voz, una entrada), selector de dificultad, puntaje visible | `fc93c6e` |

Los incrementos 1–3 se verificaron con pruebas funcionales manuales. El incremento 4 está implementado y
verificado por compilación y tests (ver §6 ter).

## 4. Estructura

```
Kantor.sln
src/Kantor.Core   (sin UI ni hardware)
  Songs/    Song, Voice, Line, Note, UltraStarParser, SongMedia
  Timing/   SongTiming (beat <-> tiempo), ISongClock, HostClock + ISongTimeMap, PlaybackClockModel,
            FrameClock (índice de cuadro <-> instante, con percentil bajo de la ventana)
  Audio/    InputChannelId, PcmDecoder, LevelMeter, ChannelAnalyzer (nivel y tono por canal, sin motor);
            AudioEngine.cs: IAudioEngine (motor), ISongPlayer, ICaptureSession, AudioInputDevice,
            AudioEngineChoice (motor pedido -> disponible, con respaldo)
  Pitch/    PitchDetector (YIN), PitchTracker, MusicalPitch, PitchTimeline
  Calibration/  señal de barridos, filtro por correlación (FFT), reglas de aceptación del retardo
  Settings/ UserSettings (+ perfil de retardo por dispositivo), UserSettingsStore
  Scoring/  Difficulty, ScoringRules, NoteScoring (regla de acierto), BeatSampler (política temporal),
            VoiceScorer (puntaje por beat y bono de línea); no conoce YIN, NAudio ni WPF
src/Kantor.App    (WPF)
  Audio/    WasapiEngine + AudioEngineFactory + AsioAvailability, AsioEngine (motor ASIO), SongPlayer,
            WasapiInputCapture, CaptureSession, ActiveInput, AudioInputCatalog, CalibrationRunner, PitchLog
  Stage/    StageControl (notas, letra, estela de tono)
  Options/  OptionsView, OptionsModel (opciones: audio, biblioteca, juego, pantalla)
  Remote/   WpfCoverProvider (miniaturas de portada para Avyn)
  Pixel/    PixelViewbox (escala cuantizada a píxeles físicos), controles pixelados
  MainWindow, MainWindow.Remote.cs (HostAdapter: la ventana vista por el controlador), CalibrationWindow
src/Kantor.Core/Remote  (control remoto, sin WPF): Protocol, Mechanisms (dedupe, presencia, revisiones), Host
            (IKantorHost/HostState), KantorController (+ .Commands), AutoAdvanceMachine, KantorIpcServer
src/Kantor.Core/Data    KantorStore (esquema v3), QueueService/QueueModels, RankingStats, modelos
src/Kantor.Core/Library SongLibrary (índice, escaneo, búsqueda), LibraryCatalog (ids estables, facetas)
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

Se necesita el SDK **.NET 10**. Si está instalado a nivel usuario (`%USERPROFILE%\.dotnet`) y no en el
PATH global:

```powershell
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
cd <carpeta del repositorio>
dotnet build Kantor.sln
dotnet test Kantor.sln
# prueba contra la biblioteca real (1000+ canciones):
$env:KANTOR_SONGS_DIR = '<carpeta con canciones>'; dotnet test Kantor.sln
# ejecutar con una canción:
src\Kantor.App\bin\Debug\net10.0-windows\Kantor.App.exe "<carpeta>\Artista - Título\Artista - Título.txt"
```

Atajos: Ctrl+O abrir, Espacio play/pausa, F11 pantalla completa, Esc salir, Ctrl+I entradas de audio.

## 6. Lo comprobado

**Con pruebas automáticas (76):** parser (BOM, UTF-8/Windows-1252, coma decimal, duetos, tags desconocidos,
advertencias), conversión beat↔tiempo, resolución de rutas sin escapar de la carpeta, detector YIN con señales
sintéticas (65–880 Hz, armónicos, silencio, ruido), decodificación hasta 8 canales, niveles,
modelo de reloj (pausa/reanudación/extrapolación), marcas de tiempo de ventana, línea temporal.

**Con una biblioteca real (`KANTOR_SONGS_DIR`):** 1068 de 1069 canciones se leen y nombran un audio válido.
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
  quedó en esas). Latencias informadas por el controlador: entrada 412 y salida 508 muestras (8,6 + 10,6 =
  19,2 ms), igual que lo medido por cable (19,2 ms, dispersión 0). Por el canal digital Loop 1: 18,6 ms
  (residuo −0,5 ms). Convivencia con Windows verificada (§6 ter). Lo que **no**
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

- Una carpeta de muestra (6 canciones, mp3 + mp4) y una biblioteca real (~1100 canciones).
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
- **Octavas (decidido tras la prueba manual)**: equivalencia por clase de nota, como WorldParty;
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
  **Perfil por dispositivo**: el retardo se guarda por combinación (`UserSettings.DelayProfiles`): con Windows,
  *salida predeterminada + entrada + canal*; con ASIO, `asio|controlador|par de salida|canal de entrada`. Las
  claves de un motor nunca coinciden con las del otro. Al abrir o cambiar la entrada se aplica el de esa
  combinación si existe; cualquier cambio manual o calibración lo actualiza. Sin perfil rige el retardo global
  **del motor**: `VoiceDelayMs` para Windows (175 por omisión) y `AsioVoiceDelayMs` para ASIO (**0**: no se
  reutiliza lo de Windows hasta calibrar).
- **Motor de audio** (`Kantor.Core.Audio.IAudioEngine`): la aplicación usa `ISongPlayer`/`ICaptureSession` que
  entrega un motor. Dos motores: **Windows (compartido)** (`WasapiEngine`, el actual y el **respaldo**) y
  **ASIO** (`AsioEngine`). La elección es **global** (`UserSettings.AudioEngine`, por omisión Windows; sin
  ajuste por canción) y se hace en "Configuración de audio" (Ctrl+I): motor, controlador (si hay varios), par
  de salida y canal de entrada. Cambiar de motor o de salida reabre todo (la reproducción se detiene). Si ASIO
  no está instalado, el selector lo muestra como "ASIO (no disponible)" y la nota explica por qué; si falla al
  iniciar o el controlador pide reiniciarse en marcha, se usa Windows y se avisa (la preferencia se conserva).
- **Motor ASIO** (etapa 2):
  - Canales de entrada y salida **descubiertos del controlador** (en la Scarlett 4i4: 6 entradas "Input 1–4,
    Loop 1–2" y 4 salidas, par 1+2 o 3+4); frecuencia 48 kHz; buffer según el controlador (192 muestras).
  - Reproducción y captura salen del **mismo callback**: la canción se decodifica entera a estéreo float de
    48 kHz en memoria (0,7 s para 235 s de mp3), de modo que el callback nunca toca el disco.
  - **Modelo de tiempos**: el cuadro de entrada `j` y el de salida `j` son contemporáneos en el controlador.
    `FrameClock` relaciona el índice de cuadro con el QPC (percentil 20 de 128 bloques, frecuencia nominal).
    Posición oída = cuadros entregados − latencia de salida informada por el controlador; instante de captura
    de una muestra = instante de su cuadro − latencia de entrada informada. Así `SongPositionAt(host)` ubica
    cada análisis de tono en lo que se estaba oyendo. La latencia de entrada se lee por reflexión de las
    capacidades del controlador (NAudio solo expone la de salida).
  - **Pausa/reanudación**: el motor sigue corriendo (silencio) y la posición se congela en el cuadro entregado;
    `SongPositionAt` devuelve `null` en pausa, como el motor de Windows. Cada tramo de reproducción guarda su
    origen, así la pausa no acumula error.
  - **Convivencia con Windows** (medida): con ASIO corriendo, WASAPI compartido sigue pudiendo reproducir y
    capturar por la misma interfaz, y ASIO arranca con una captura de Windows ya abierta.
  - Calibración ASIO con el mismo método de barridos, en cuadros del propio controlador; sugiere
    `medido − latencias compensadas por el controlador`.
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
  (2) ~~solo la voz 0 y una entrada~~ resuelto: ver §8 octies (varios micrófonos; el reparto micrófono→voz es fijo);
  (3) con notas solapadas de distinto
  tono el máximo no es alcanzable (WorldParty puntúa cada nota por separado, igual); (4) la compuerta
  de nivel y la latencia acústica no están calibradas con el micrófono del usuario.

### Verificación técnica del motor ASIO (sin prueba manual todavía)
- Posición contra reloj de pared: −10 a −14 ms constantes (latencia de salida), **sin deriva** en 7 ciclos de
  pausa/reanudación; congelada en pausa; `SongPositionAt` = `null` en pausa.
- Captura multicanal (6 canales) con nivel por canal y tono en los elegidos; 0 cuadros sin ubicar en 50 s.
- Calibración dentro de la aplicación: 19 ms (± 0), compensados 19,2 → sugerido 0 ms; el perfil se guarda con la
  clave ASIO sin tocar el de Windows. Cambio de par de salida y de motor (en ambos sentidos) probado por la
  interfaz; el retardo de ASIO arranca en 0 y el de Windows (175) se conserva.
- Respaldo: pedir el controlador "Focusrite Thunderbolt ASIO" (sin hardware) cae a Windows con el motivo.
- Límite conocido: la calibración con el cable por Windows no pudo repetirse porque la salida de Windows está
  silenciada en esta máquina (volumen 0 %); el código de `CalibrationRunner` no cambió.
- La curva de puntaje de la canción devuelta por Loop 1 pica en ~60 ms y es ancha: sesgo de usar la mezcla
  con YIN y del GAP del archivo (mismo orden que con la voz), no del motor.

## 8 ter. Identidad visual 16-bit (en curso, a validar visualmente)

Orden: (1) sistema visual, (2) resultados 1 jugador preparada para 2, (3) menú principal. El escenario
durante la canción **no se toca** hasta el video de fondo. Video y dos jugadores son funciones aparte.

- `src/Kantor.App/Theme.xaml`: paleta (P1 `#19D3FF`, P2 `#FF3DB8`, oro, noche, panel, tinta), `PixelFont`
  (Jersey 15, embebida; OFL 1.1 en `Assets/Fonts/OFL.txt`), `BodyFont` (Segoe UI) y el estilo `PixelButton`.
- `src/Kantor.App/Pixel/`: `PixelFrame` (marco con esquinas en escalera, borde, relieve y sombra dura),
  `PixelText` (texto con sombra/contorno duros), `PixelCharacter` (personaje de cuerpo entero sin marco: acepta PNG transparentes de cualquier proporción; sin imagen dibuja
  un personaje provisional, con sombra pixelada bajo los pies; sobresale por arriba sin tapar datos), `PixelBackdrop` (fondo a 180 px lógicos de alto ampliado sin suavizado; 15 fps y solo
  mientras es visible).
- `src/Kantor.App/Results/`: `ResultsView` (composición de 1280×720 con `Viewbox`; fondo a pantalla completa),
  `PlayerResultPanel` (columna por jugador), `PlayerResult`/`RankPalette`. Admite 1 o 2 jugadores, ganador y empate.
- `Kantor.Core/Results/ResultRank.cs`: 8 sellos por rango (independientes del comentario) y textos fijos es/en.
- Se usa en `MainWindow` al terminar la canción; "Otra vez" = Detener + Reproducir; Escape o "Cerrar" la oculta.
- **Saltear la animación**: Enter o Espacio durante la secuencia la completan sin activar botones (tampoco con la
  repetición ni al soltar); una pulsación posterior activa el botón con foco; Escape cierra. La lógica está en
  `Kantor.Core/Results/ResultsInputGate.cs` (probada). `MainWindow` no consume Espacio/Enter/Escape con la
  pantalla visible.
- **Sellos en español** vigentes: SISTEMA INTACTO · ¡AL MENOS LO INTENTASTE! (se parte en dos líneas) ·
  APROBADO... CREO · ¡NADA MAL! · ¡IMPRESIONANTE! · ¡SUPERSTAR! · ¡LEGENDARIO! · ¡PERFECTO!
- **Layout**: la composición mide 1280 de ancho y crece en alto si hace falta (comentarios largos); el `Viewbox`
  la reduce completa. Alturas reservadas (etiqueta y sello) para que las columnas queden alineadas.
- **Escalado**: con factores no enteros (p. ej. 1366×768 → ×1,067) el borde sigue nítido (sin suavizado) pero los
  escalones de 4 px salen de 4 o 5 px. Propuesta pendiente: cuantizar el factor a múltiplos de 0,25.
- **Resultados con 2 a 4 jugadores (ceremonia)**: clasificación fija a la izquierda (posición con empates
  compartidos, nombre, puntaje, color; fila actual resaltada) y presentación individual a la derecha (avatar,
  nombre, puntaje, sello, comentario). Orden: se revela la clasificación; ganador; luego de mayor a menor (los
  empatados en el primer puesto, consecutivos); al final vuelve el ganador y la pantalla queda estable. Cada
  comentario permanece 4 s desde que termina su animación. Un jugador: composición centrada, sin tabla.
  Lógica en `Kantor.Core/Results/ResultsCeremony.cs` y `ResultStandings.cs` (probada).
- **Teclas**: Enter/Espacio completan la animación en curso; con la animación terminada, avanzan al siguiente
  participante (la pulsación no activa botones); `Fin` saltea toda la ceremonia; Escape cierra; con la ceremonia
  terminada, Enter/Espacio activan el botón con foco.
- **Comentarios en Jersey 15** (sin sombra ni contorno, interlineado 1,12). Segoe UI queda para letras y
  textos funcionales.
- **Colores de jugador**: J1 `#19D3FF`, J2 `#FF3DB8`, J3 `#5BE56B`, J4 `#A06BFF` (`PlayerColors`).
- **Ajustes opcionales por personaje** (`CharacterTuning` en `PlayerResult`, sin interfaz): `VisualScale` (1 =
  sin cambio; agranda personajes anchos), `GroundOffset` (línea de pies; positivo = más abajo) y `Overflow`
  (desborde superior, 0,12 por defecto; límite ajustable, el alto nunca lo supera). Geometría pura y probada en
  `Kantor.Core/Results/CharacterPlacement.cs`. No hay dimensiones obligatorias para los PNG: el componente se
  adapta a cualquier proporción; se recomienda recorte ajustado y transparencia real.
- **Trampa**: Jersey 15 solo tiene peso regular; pedir Bold genera negrita sintética (por eso `PixelText` usa Normal).
- **Personajes (modelo)**: cada personaje tiene dos recursos que comparten identidad: `Portrait` (cabeza y
  hombros; selección y reproducción, aún sin usar) y `FullBody` (ceremonia de resultados). Definición central en
  `Kantor.Core/Characters/` (`CharacterDefinition` con ID estable, nombre, rutas opcionales y `CharacterTuning`;
  `CharacterCatalog` valida IDs únicos). Hoy el catálogo solo trae el personaje `provisional`, sin recursos.
  Los recursos se resuelven en `Kantor.App/Characters/CharacterAssets.cs` desde `Assets/Characters/{id}/
  portrait.png` y `fullbody.png` (embebidos como `Resource`); si faltan queda `null` y se usa el placeholder.
  Sin selección de personajes ni cambios en la pantalla de reproducción todavía.
- **Distribución**: `scripts/publish.ps1` arma `dist\Kantor-<versión>-win-x64\` (autocontenida, solo x64, sin .pdb,
  sin `libvlc\win-x86`) y su `.zip`; ver `docs/DISTRIBUCION.md` (incluye lo que falta para la Store). Versión en
  `Kantor.App.csproj` (0.1.0). Comprobado: publica y arranca desde `dist`; sin probar en una PC limpia ni firmada.
- **Señal de onda del micrófono** (`StageControl.DrawWave`): a la derecha de la tarjeta de cada jugador con
  micrófono, la envolvente de los últimos ~1,3 s (22 columnas simétricas, vieja → ahora) en el color del jugador,
  sin marco ni fondo (cada columna con contorno oscuro). Sale de `PitchSample.LevelDb` de su `PitchTimeline` (sin otra captura): -55 dB → mínimo,
  -10 dB → altura completa; con tono lleno, sin tono (ruido) apagado. Sin comprobar a mano con voz real.
- **Mensajes por frase durante el canto** (`Kantor.Core/Feedback/`): al cerrarse una frase puntuable aparece una
  valoración breve a la derecha de la tarjeta del jugador (franja sin notas ni letra), con rebote de ~0,25 s,
  1,4 s de vida y desvanecido; la vida se mide con la posición de la canción (un retroceso o reinicio los apaga).
  - **Porcentaje**: `VoiceScorer.LineResults` (`LineResult.Fraction`) = peso acertado de la frase / peso posible de
    la frase, con los mismos pesos del puntaje (normal 1, dorada 2, rap 1, rap dorado 2; freestyle no cuenta).
    Es relativo a la propia frase (una corta no se penaliza) y no incluye el bono de línea ni toca el puntaje. Una
    frase sin notas puntuables no genera resultado. Se vacía con `Reset` (sube `Generation`).
  - **Rangos** (límite inferior incluido): Perfecto ≥ 98 · Excelente ≥ 90 · Muy bueno ≥ 80 · Bueno ≥ 65 ·
    Aceptable ≥ 50 · Flojo ≥ 30 · Malo ≥ 10 · Desastre < 10 (`PhraseRatings`).
  - **Selección**: aleatoria dentro del nivel, sin repetir los últimos 4 mensajes de ese jugador (historial por
    jugador). Catálogo separado (`PhraseFeedbackCatalog`, máx. 3 palabras). Hay dos catálogos **independientes**
    (español e inglés, cada uno con su humor; no son traducción uno del otro). Se elige por el idioma de la
    interfaz (`CommentLanguage`, no el de la canción) con `PhraseFeedbackCatalog.For`; sin catálogo para ese
    idioma, español. Inglés: 96 valoraciones (12 × 8) + 8 racha + 8 recuperación + 8 duelo.
  - **Especiales** (prioridad duelo > recuperación > racha > individual): *racha* cada 4 frases ≥ 80 % seguidas
    (no reemplaza a un Perfecto); *recuperación*: frase ≥ 80 % tras 2 frases < 50 % seguidas; *duelo*: dos
    jugadores con frase perfecta en el mismo cierre. Solo mira esos hechos, no compara puntajes.
  - **Obsoletos**: solo se muestra la última frase de cada jugador por sondeo y solo si cerró hace ≤ 2 s
    (`PhraseFeedbackTracker.MaxAge`); tras salto, pausa larga o reactivar la opción no aparecen viejos.
  - **Opción**: OPCIONES › Juego › "Mensajes por frase al cantar" (activa por omisión, `UserSettings.PhraseFeedback`).
  - Sin sonidos. Sin comprobar a mano: el aspecto con video de fondo y con 3–4 jugadores.
- **Personajes propios**: el usuario carga los suyos en JUGADORES › "Cargar personaje" (cuerpo entero y/o cara; si
  falta la cara se recorta, siempre cuadrada, del cuerpo; o se carga aparte y también se recorta). Se guardan en
  `%APPDATA%\Kantor\Characters\usuario-<hex>\` (`fullbody.png` ≤ 1400 px de alto, `portrait.png` ≤ 256 px,
  sin nombre) por `UserCharacterStore` (Core). **Los avatares no tienen nombre visible**: el selector es visual
  (`FaceChoice`: grilla de caras, sin texto); internamente `Name` = ID. `CharacterCatalog.Default` suma los propios con
  `SetUserCharacters` (se cargan al abrir el historial); `CharacterDefinition.IsUser` marca los borrables y sus
  rutas son absolutas (`CharacterAssets` las lee como archivo). Recorte: `SquareCrop` (Core, probado) y
  `CropEditor` (App: mouse arrastrar/esquina/rueda; teclado flechas y + −). Sin ajustes de escala/pies propios.
  Sin comprobar a mano: el modal y el recorte con imágenes reales.
- **Trampa**: `pack://application:,,,/Assets/Fonts/#Jersey 15` se resuelve contra el ensamblado de entrada; un
  arnés externo debe incluir las fuentes en su propio ensamblado.


## 8 quater. Menú principal

- Vista `Kantor.App/Menu/MainMenuView` (dentro de `MainWindow`, a pantalla completa sobre barra y estado): logo
  (`Assets/Branding/kantor-logo.png`, con su halo original), `CANTAR` / `AUDIO` / `SALIR`, fondo de escenario y
  navegación con flechas + Enter o mouse. Textos es/en en `Kantor.Core/Menu/MenuTexts.cs` (usa el idioma de
  "Comentarios").
- Flujo: arranque → menú (si no se pasa una canción por argumento) → `CANTAR` abre el selector de canción (hoy;
  luego, la biblioteca) → escenario → resultados → `CERRAR` vuelve al menú. Botón "Menú" en la barra para volver
  en cualquier momento (detiene la canción).
- Sin comprobar a mano: foco inicial y navegación con teclado dentro de la `MainWindow` real, y que
  `CANTAR` cancelado deja el menú en pantalla.

## 8 quinquies. Video de fondo

- `#VIDEO` y `#VIDEOGAP` se leen en el parser (`Song.VideoFile`, `Song.VideoGapSeconds`; coma o punto decimal).
  Regla de sincronización en `Kantor.Core/Songs/VideoSync.cs` (probada): el video se muestra en
  `posición del audio + VIDEOGAP`; tolerancia 350 ms antes de corregir saltando el video.
- `Kantor.App/Video/VideoBackground.cs`: libVLC (paquetes NuGet `LibVLCSharp` 3.9.4 y `VideoLAN.LibVLC.Windows`
  3.0.21, LGPL-2.1+) decodifica sin audio a un búfer de 32 bits (ancho máx. 960) que se dibuja en un
  `WriteableBitmap`; así la interfaz se superpone sin problemas de ventana. El audio de la canción manda: el video
  sigue su reloj (`Sync`, ~30 veces por segundo), con saltos de corrección como mucho cada 1 s. Si libVLC no está
  disponible o el video falla, queda deshabilitado y el escenario se ve como siempre.
- El escenario pasa a fondo translúcido cuando hay video (`StageControl.TranslucentBackground`). El rediseño
  visual del escenario sigue pendiente (se hace sobre esto).
- Comprobado con `30 Seconds To Mars - Attack` (AVI Xvid, `#VIDEOGAP:1`): se ve el video y avanza con la canción.
  Sin comprobar a mano: precisión de sincronía (ojo/oído), otros formatos (mp4, flv, mpg, webm), consumo de CPU,
  pausa/reanudación y seek con distintos códecs. La salida de compilación crece ~200 MB por las bibliotecas nativas.
- **Trampa**: `ParseFile` reconstruye el `Song` para fijarle `Directory`; todo campo nuevo del `Song` tiene que
  copiarse ahí (hay prueba).

## 8 sexies. Biblioteca de canciones

- Core (`Kantor.Core/Library/`): `SongHeaderReader` (solo la cabecera; exige #BPM y audio; carátula por `#COVER`
  o por convención `<nombre>.jpg` / `<nombre> [CO].jpg`), `SongLibraryScanner` (recursivo, deduplica),
  `SongSearch` (todas las palabras, sin importar mayúsculas ni acentos, en artista/título/género/año/idioma/
  edición; los que empiezan con la consulta van primero), `SongIndexStore` (índice `library-index.json` junto a
  los ajustes; reutiliza lo ya leído si el archivo no cambió: misma fecha y tamaño).
- Ajuste nuevo: `UserSettings.LibraryPaths` (carpetas). Primer uso de "CANTAR" sin carpetas: pide una.
- UI (`Kantor.App/Library/SongListView`): buscador siempre enfocado, lista con carátulas y marca de video, panel
  de detalle. Teclas: ↑↓ / RePág AvPág, Ctrl+Inicio/Fin, Enter canta, Esc limpia la búsqueda o vuelve, F2 suma una
  carpeta. Doble clic también canta.
- Medido con la biblioteca real (1068 canciones): escaneo en frío ~18 s (disco), en caliente 0,3 s; con índice la
  lista sale al instante y se refresca en segundo plano. Filtrar: ~5 ms.
- Vistas: lista o grilla de 5 columnas, ambas con panel de detalle a la derecha (título completo) (F3 o botón; se recuerda en
  `UserSettings.LibraryGrid`). La grilla virtualiza por filas. Barra de desplazamiento propia (carril oscuro y
  pulgar cian escalonado; dorado al agarrarlo), definida dentro de `SongListView`.
- Tipografía: títulos, artistas, buscador y detalle en Jersey 15 (los nombres de canciones son textos cortos);
  Segoe UI queda para letras y textos largos.
- Pendiente/Sin comprobar a mano: foco y teclado dentro de la `MainWindow` real, cancelar el selector de carpeta,
  barra de desplazamiento con el estilo del tema (hoy usa la estándar de WPF).

## 8 septies. Escenario durante la canción (rediseño arcade)

- `StageControl` redibujado conservando la lógica (posición, barras por beat, estela cruda, letra resaltada):
  notas como "ranuras" (borde de color, relleno oscuro, esquinas escalonadas y sombra dura; doradas en oro, rap en
  naranja, activa en blanco), voz acertada en cian sólido con contorno y fallo al 65 % de altura, línea de ahora en
  oro con rombos, franja translúcida detrás de las notas (el video se ve), letra con contorno oscuro y franja
  propia (cantado en oro), barra de progreso en bloques.
- HUD (dentro del escenario, así se ve también en pantalla completa): retrato (hoy silueta; `PlayerPortrait`
  preparado para el PNG definitivo), nombre, puntaje grande, precisión (aciertos/evaluados) y título/artista.
- Texto largo y letras siguen en Segoe UI; Jersey 15 solo en nombre, puntaje, título y precisión.
- **Trampa**: un marco con relleno translúcido debe dibujar el borde como anillo (even-odd); si el borde es un
  rectángulo completo opaco, tapa el video.
- Sin comprobar a mano: pantalla completa, canciones de varias voces (dúos), dificultades altas con muchos
  tonos, y legibilidad sobre videos muy claros durante una canción completa. El HUD repite puntaje y título de
  la barra de herramientas en modo ventana (se ocultan en pantalla completa).
## 8 octies. Varios micrófonos simultáneos (hasta 4)

- **Modelo** (`Kantor.Core/Audio/MicrophoneMap.cs`, probado): 4 *puestos* (0–3); cada uno apunta a un canal de un
  dispositivo (`MicrophoneSource`). Un canal alimenta un solo puesto (asignarlo a otro se lo quita al anterior);
  puede haber puestos vacíos en el medio. Sin sesiones todavía, el puesto *n* es "Jugador n+1", con su color
  (`PlayerColors`). Decisión: los jugadores serán históricos y habrá sesiones más adelante
  (continuar o crear sesión al entrar en "Cantar", elegir jugadores, estadísticas globales y por sesión); **nada
  de eso está hecho**.
- **Captura** (`ActiveInput`, reescrito): una sola sesión por dispositivo, con los canales con puesto como canales
  de tono (varios micrófonos del mismo dispositivo o controlador ASIO comparten sesión); una `PitchTimeline` por
  puesto (`TimelineOf`). `Assign`/`Unassign`/`Close`; `Preview(dispositivo)` abre un dispositivo solo para ver
  niveles (la ventana de audio lo usa al elegir un dispositivo sin micrófonos). Cerrar el último canal de un
  dispositivo cierra su captura.
- **Voz que canta cada puesto** (`VoiceAssignment.VoiceFor`): canción de una voz, todos cantan la 0; dueto, el
  puesto 0 la voz 0, el 1 la voz 1 y así alternando. Es un reparto fijo: no se elige por canción.
- **Puntaje**: un `VoiceScorer` por jugador (mismo reglamento, mismo retardo de ese micrófono). Sin micrófono se
  muestra el puesto 0 en cero, como antes. Cambiar la asignación de micrófonos reinicia el puntaje.
- **Retardo por micrófono**: el selector "Retardo" y "Calibrar…" gobiernan el **primer micrófono abierto**
  (`PrimarySlot`); los demás usan su perfil por dispositivo/canal si existe, o el retardo global del motor.
  Calibrar el segundo micrófono por separado todavía no tiene interfaz.
- **Escenario** (`StagePlayer`, `StageControl.Players`): un chip de HUD por jugador (nombre, puntaje, precisión; el
  espacio se reparte y la canción cede ancho); las barras de voz de varios jugadores en la misma voz comparten la
  altura de la nota en franjas, una por jugador y cada una en su color. La estela *Cruda* ahora usa el color del
  jugador (antes magenta fijo; con J1 en cian se confunde más con el borde de las notas).
- **Resultados**: un `PlayerResult` por jugador con su comentario sorteado; la pantalla ya hacía la ceremonia de 2
  a 4. El nombre en la barra técnica muestra "J1 1.234 · J2 987".
- **Ajustes**: `UserSettings.Microphones` (puesto, dispositivo, canal). Si no existe, se migra `InputDeviceId` /
  `InputChannel` como puesto 0 sin reescribir el archivo hasta que el usuario cambie algo. Al arrancar se abren los
  micrófonos cuyo dispositivo sigue disponible; reabrirlos no cuenta como cambio, así un dispositivo desenchufado
  no borra lo guardado. `KANTOR_PITCH_LOG` suma la columna `slot`.
- **Ventana de audio** (`InputWindow`): por cada canal del dispositivo elegido, un selector "Sin usar / Jugador 1–4",
  nivel por canal y, abajo, una fila por micrófono activo con la nota que detecta.
- **Comprobado**: compila sin advertencias; 384 tests (nuevos: mapa de micrófonos, reparto de voces, migración y
  normalización de ajustes); la app arranca con una canción y con la configuración anterior.
  **Sin comprobar** (requiere hardware y manos): dos micrófonos a la vez en la Focusrite (canales 1 y 2, WASAPI y
  ASIO), dos dispositivos distintos a la vez, la apariencia de las franjas con 2–4 jugadores y del HUD con varios
  chips, cantar un dueto con dos micrófonos, y quitar un micrófono con la canción sonando (reinicia el puntaje).
- **Pendiente propio**: elegir qué voz canta cada micrófono. (El retardo y la calibración por micrófono y la
  pantalla de Opciones están hechos: ver §8 nonies.)

## 8 nonies. Opciones

- **Guía** (pestaña `GUÍA`/`GUIDE`, `Section.Help`): cinco temas de texto (canciones, micrófonos, retardo, cantar/partida,
  control remoto), claves `help.*` en `OptionsTexts`. Sin controles enfocables: con el foco en la pestaña, ↑↓ desplazan
  y la rueda también. Se abre con el botón **?** arriba a la izquierda del menú principal. Pantalla de canciones: el
  contador "N canciones" pasó de la cabecera al buscador (la cabecera se quedaba sin ancho). Sin ver en pantalla.

- `Kantor.App/Options/OptionsView` (+ `OptionsModel`) reemplaza a la ventana "Configuración de audio" (borrada). El
  menú principal pasa de `AUDIO` a `OPCIONES`; Ctrl+I y el botón "Opciones…" de la barra abren la sección Audio.
  **Abrirla detiene la canción** (como "Menú"): cambiar micrófonos, dificultad o retardo reinicia el puntaje.
- Controles: `Pixel/ChoiceButton` (**select**: muestra el valor con una flecha y, con clic, Enter o Espacio,
  despliega la lista; ↑↓ recorren, Enter elige, Esc cierra; **no hay selectores rotativos** en ningún lado) y
  `PixelMeter` (nivel en bloques). **Navegación con teclado** (Opciones y Jugadores): el foco arranca en la
  pestaña activa; **← →** cambian de pestaña, **↓** (o Enter) entra a las opciones, **↑** desde la primera vuelve a
  las pestañas, **↑↓** recorren las opciones, **Enter** abre/activa (en un select despliega la lista), **Esc**
  cierra lo que esté abierto encima y, si no hay nada, vuelve (RePág/AvPág también cambian de pestaña). Textos es/en en
  `Kantor.Core/Menu/OptionsTexts.cs` (probado: toda clave tiene ambos idiomas); las notas técnicas del motor
  (descripción de Windows/ASIO) siguen solo en español.
- **Audio**: motor, controlador ASIO (si hay varios) y par de salida; después **Micrófonos**. Un micrófono es lo
  que se **crea** ("Crear micrófono", hasta 4: M1–M4) y se define con un **dispositivo + un canal** (dos
  selectores en su renglón, más "Quitar"). Debajo de cada uno: nivel en vivo, nota y Hz, **retardo** (0–300 ms)
  y **Calibrar** propios. **Los micrófonos creados son los habilitados durante el juego**: definen cuántos
  jugadores pueden cantar a la vez y qué renglones aparecen en "¿Quién canta?". Un canal solo lo usa un micrófono
  (si se intenta repetir, se avisa y vuelve al anterior). Crear toma el primer puesto libre y el primer canal
  libre; al cambiar de dispositivo se elige su primer canal libre. Se eliminó la vista previa de dispositivos
  (`ActiveInput.Preview`) y la asignación canal → jugador de esta pantalla (el jugador se elige por canción).
- **Biblioteca**: carpetas (agregar con selector, quitar) y vista lista/grilla. **Juego**: dificultad, voz sobre las
  notas, **idioma** (es/en; el mismo ajuste que ya gobernaba menú, resultados y comentarios) y probabilidad del
  comentario disparatado (0/1/3/5/10 %). **Pantalla**: iniciar en pantalla completa (`UserSettings.StartFullscreen`,
  nuevo) y barra técnica.
- Los selectores de la barra técnica (dificultad, retardo, voz, idioma) siguen existiendo y quedan sincronizados:
  Opciones los maneja a través de ellos. Retardo: el micrófono principal usa el selector (y el retardo global del
  motor); los demás, su perfil por dispositivo/canal.
- Comprobado con la app real (capturas): las cuatro pestañas dibujan bien, el medidor de nivel de la Focusrite se
  mueve y la asignación de canal a jugador se muestra. **Sin comprobar a mano**: cambio de motor a ASIO desde la
  pantalla, calibrar desde un renglón, agregar/quitar carpetas, cambiar de idioma en vivo y dos micrófonos.
  **Barra de desplazamiento**: estilo `PixelScrollBar` del tema (carril oscuro, pulgar cian escalonado, dorado al
  agarrarlo; `Pixel/PixelScrollTrack`), usado en Opciones, Jugadores y la lista de canciones. Es una clave, no un
  estilo implícito: se aplica con `<ScrollViewer.Resources>` a los visores verticales (una barra horizontal usaría
  el aspecto estándar). **Trampa**: el track se dibuja antes de engancharse a la barra; `Attach()` tiene que
  invalidar el dibujo o el pulgar no aparece hasta el primer cambio de valor.

## 8 decies. Jugadores, sesiones e historial (SQLite)

**Alcance de esta etapa**: perfiles persistentes, sesiones que sobreviven al cierre, selección P1–P4 e historial
básico. Sin rankings globales, gráficos ni estadísticas agregadas (nada agregado se guarda: se calcula al consultar).

### Flujo al entrar a una canción (vigente)
1. Elegir canción (biblioteca, Ctrl+O o línea de comandos) → carga y **enseguida pregunta**; no hace falta Reproducir.
2. Si todavía no se decidió cómo cantar: puerta (Continuar partida / Nueva partida / Cantar suelto).
3. **Nueva partida → "¿QUIÉN JUEGA?"**: los jugadores ya cargados con un interruptor "En la partida / Fuera" (con
   su retrato) o **crear uno nuevo eligiéndole nombre y avatar** (los jugadores que se crean ahí entran a la
   partida). Hace falta al menos uno.
4. **"¿QUIÉN CANTA?"**: un tile cuadrado por micrófono creado ("MIC 1", retrato y nombre del jugador asignado);
   al tocarlo se abre un **modal** con una tarjeta (retrato + nombre) por jugador de la partida y "Nadie". Un
   jugador ocupa un solo micrófono. Sin micrófonos creados, avisa y ofrece ir a Opciones › Audio.
5. Confirmar (LISTO) **empieza la canción**; Esc deja la canción cargada sin empezar.
- **Sin barra de herramientas**: la franja de botones de arriba ya no se muestra (el escenario ocupa todo). Sus
  controles siguen en el árbol como estado de dificultad/retardo/voz/idioma, que se manejan desde Opciones.
  Teclas: **Espacio** pausa/reanuda, **Esc** pausa (si suena) y después vuelve al menú (detiene la canción; en
  pantalla completa, primero sale de ella), **Ctrl+O** abrir canción, **Ctrl+I** Opciones, **Ctrl+J** Jugadores,
  **F11** pantalla completa, **F9** barra técnica.
- **Todo se maneja con mouse o con teclado**. Cada pantalla tiene su botón **VOLVER** (Opciones, Jugadores en
  todas sus pestañas y modos, lista de canciones, puerta de partida; los modales tienen Volver/LISTO y Esc). En el
  escenario, al mover el mouse aparecen **PAUSA/SEGUIR/CANTAR** y **MENÚ** (abajo a la derecha; se quedan mientras
  la canción no suena). **Trampa**: el desplegable es un `Popup` (otra ventana, no hereda la escala del `Viewbox`
  ni recibe el foco de la pantalla): se le aplica la escala a mano y las pantallas ignoran Esc/flechas mientras
  hay uno abierto (`ChoiceButton.AnyOpen`).

### Escenario con varios jugadores (como WorldParty)
- **Un carril por jugador** que canta una voz: cada uno con las notas y sus propias barras (a altura completa, en su
  color) y, **arriba de su carril, una tarjeta** con retrato, nombre, puntaje y precisión (marco del color del
  jugador); la letra es compartida, debajo. En un dueto cada voz es su panel y sus jugadores sus carriles. Ya no hay
  barra de chips arriba: solo artista y título en una línea chica, sin fondo ni marco (texto con contorno).
  Referencia: una captura de WorldParty (2 jugadores, carriles apilados, avatar y puntaje por jugador, letra abajo);
  no se tuvo su código a mano. Sin comprobar con 3 o 4 jugadores en una voz.
- **Congelados al elegir canción**: `SongPlayer.Load` abría el archivo dentro de `lock(_sync)`, el mismo que la
  interfaz toma cada 33 ms; ahora lo abre fuera. El aviso de escaneo de la biblioteca se atenúa (1 de cada 25).
  Medido: abrir el audio de las canciones probadas tarda 1–250 ms, así que no se reprodujo un parón largo; si
  vuelve, hace falta saber con qué canción y motor.

### Carga de canciones y vista previa
- **Carga en segundo plano**: `LoadSong` lee el TXT, valida el audio y lo decodifica (`ISongPlayer.Load`; con ASIO,
  la canción entera) en un `Task`, con el cartel "CARGANDO…"; recién al terminar se arma el escenario en el hilo de
  la interfaz (`CompleteLoad`/`FailLoad`). Una carga que quedó vieja (se eligió otra canción o se volvió al menú)
  se descarta con `_loadVersion`. Medido (canciones con video de la biblioteca, 3 de 3): lectura+audio ~40–110 ms,
  libVLC la primera vez ~190 ms; el cuelgue que se veía con ASIO era la decodificación en el hilo de la interfaz.
- **Vista previa en la lista** (`Audio/SongPreview`, `SongHeaderReader.ReadPreview`): al detenerse 450 ms sobre una
  canción suena un tramo desde `#PREVIEWSTART` (o un cuarto de la canción) con entrada suave y **volumen 0,35**
  (la canción va a 1,0), por la salida predeterminada de Windows (no toca el motor de la canción ni la captura).
  Se corta al elegir, volver, cambiar de pantalla o apagarla en Opciones › Juego ("Vista previa en la lista",
  `UserSettings.SongPreview`, por omisión sí). Sin comprobar a oído en esta máquina.

### Nombre en pantalla
- En español la "sesión" se llama **Partida** (decisión: "sesión" suena a sistema; "Continuar
  partida / Nueva partida" es lo que ya conocen de los videojuegos) y cantar sin ella es **"Cantar suelto"**.
  En inglés queda "Session" / "Sing without session". En el código, el esquema y esta documentación el concepto
  sigue siendo *session* (Partida = `session`); solo cambian los textos de `PlayersTexts` y los mensajes de
  `KantorStore`.

### Decisiones
- No hay participantes anónimos: cantar requiere jugadores elegidos. **En cada canción** se elige quién canta,
  sobre los **micrófonos habilitados** (Opciones › Audio): al empezar una interpretación de cero (Espacio o
  Reproducir) aparece "¿QUIÉN CANTA?" con la elección anterior cargada. No se pide al abrir la aplicación ni la
  biblioteca. "Otra vez" (misma canción) no vuelve a preguntar.
- **La sesión se decide al entrar a CANTAR, cada vez que se entra desde el menú** (también al dar Reproducir con una
  canción abierta por Ctrl+O o por línea de comandos, una vez): con una sesión abierta, CONTINUAR SESIÓN (recupera sus jugadores y
  la selección), INICIAR NUEVA (cierra la anterior, sin heredar jugadores) o CANTAR SIN SESIÓN; sin sesión abierta,
  INICIAR SESIÓN o CANTAR SIN SESIÓN. Esc cancela sin decidir. La pestaña JUGADORES › Partida es solo de la
  partida (estado, nueva/cerrar, sus jugadores): **no tiene un selector "con partida / suelto"**, porque suelto es
  estar fuera de una partida. Para pasar a cantar suelto una vez decidido: cerrar la partida (sin partida abierta
  se canta suelto) o reiniciar Kantor y elegir CANTAR SUELTO en la puerta.
- **Cantar sin sesión**: se puede elegir cualquier jugador del sistema, la sesión abierta no se toca y **el
  resultado no se guarda** (una interpretación cuelga de una sesión). Se avisa en el menú y al terminar.
- **Las sesiones sobreviven al cierre de Kantor**: nunca se cierran solas; cerrar es una acción explícita (o
  "iniciar nueva"). Una sola sesión abierta a la vez. La aplicación **ya no crea sesiones automáticas** al
  terminar una canción (`KantorStore.SavePerformance` conserva esa capacidad y sus pruebas, pero no se usa).
- **Una sesión admite cualquier cantidad de jugadores**; el límite de 4 es por interpretación (puestos P1–P4 =
  micrófonos habilitados). Cada canción puede cantarla un subconjunto distinto de los jugadores de la sesión.
- **Quién canta en cada micrófono se elige por canción, no por sesión**: solo existe en la pantalla "¿Quién
  canta?" al empezar cada canción (la pestaña Sesión de Jugadores ya no tiene ese selector). La última elección
  queda cargada como comodidad (en memoria, y guardada en la sesión para recuperarla al continuar), pero siempre
  se confirma al entrar a una canción nueva.
- **Solo se guarda una interpretación terminada**: se exige que la última posición vista esté a ≤ 1 s de la
  duración (tras el fin natural la posición vuelve a 0, así que se recuerda la última del temporizador de
  puntaje). Detener, Menú, Opciones, error o desconexión no dejan nada, ni parcial.
- Hasta 4 participantes por interpretación. **Puesto de micrófono (P1–P4) ≠ posición en la clasificación**: la
  posición se calcula con empates compartidos (1, 1, 3) y no se guarda.
- Un puesto sin jugador asignado **no figura durante el canto** (ni HUD, ni puntaje, ni resultados); tampoco un
  jugador elegido cuyo puesto no tenga micrófono (se avisa en la selección). Puesto *n* = micrófono *n* de Opciones.
- **Los participantes salen solo de los jugadores de la sesión**. Elegir no incorpora: hay que agregarlos a la
  sesión explícitamente (JUGADORES › Sesión › "Agregar a la sesión", o creando un jugador desde "¿Quién canta?",
  que entra a la sesión). Cantando sin sesión se elige entre todos los jugadores activos del sistema.
- **ID de canción** = SHA-256 de artista + título normalizados (`SongIdentity`, sobrevive a mover carpetas;
  dos ediciones con el mismo artista y título se funden). Se guardan además título y artista como instantánea.
- Fallo técnico al guardar (base bloqueada, disco): **un reintento** y se avisa en la barra técnica; una regla
  incumplida no se reintenta. Un mismo fin nunca se guarda dos veces (id de ejecución = clave primaria).
- Personaje: se guarda el `character_id` tal cual; el fallback (`CharacterCatalog.GetOrDefault`) se aplica solo al
  mostrar, así un personaje que desaparece del catálogo no se pierde. Hoy solo existe `provisional`.

### Esquema (v1, `PRAGMA user_version = 1`; `%APPDATA%\Kantor\kantor.db`, junto a `settings.json`)
| Tabla | Contenido y restricciones |
|---|---|
| `player` | id, nombre (1–24 car.), `character_id`, `created_at`, `last_activity_at`, `archived`. Índice único de nombre (sin distinguir mayúsculas) **solo entre activos**. |
| `session` | id, nombre opcional, `started_at`, `closed_at`, `is_open`. CHECK `is_open = (closed_at IS NULL)`; **índice único parcial** `WHERE is_open = 1` ⇒ una sola abierta. |
| `session_player` | (session, player) PK, `joined_at`. Quién pertenece a la sesión. |
| `session_selection` | (session, slot 1–4) PK, UNIQUE (session, player), FK compuesta a `session_player`. La selección activa. |
| `performance` | id TEXT (id de ejecución), session, `song_id`, título y artista (instantánea), `finished_at`, dificultad 0–2, `scoring_version`. Índices por (sesión, fecha) y por canción. |
| `performance_result` | (performance, slot 1–4) PK, UNIQUE (performance, player), player, score 0–10000. Trigger: rechaza jugadores archivados. Índice por jugador. |
- Todas las FK son `ON DELETE RESTRICT`; `PRAGMA foreign_keys=ON` en cada conexión. Archivar nunca borra historial
  (solo marca y quita al jugador de la selección de la sesión abierta).
- Guardar interpretación + resultados (y la sesión automática si hace falta) es **una transacción**: validación de
  jugadores existentes/activos/miembros de la sesión dentro de ella; si algo falla no queda nada.
- Migraciones: lista ordenada de scripts en `KantorStore`; una base de esquema más nuevo que el conocido se rechaza.
- `ScoringRules.ScoringVersion = 1` (constante nueva; el algoritmo no se tocó). Subirla cuando cambie una regla.
- Dependencia nueva: `Microsoft.Data.Sqlite` 10.0.13 (MIT) + SQLitePCLRaw (Apache-2.0), compatibles con GPL-3.0+.
  Se descartó la 9.0.0 por la advertencia NU1903 de su biblioteca nativa; la 10.0.13 no tiene vulnerabilidades.

### Datos y pruebas
- Carpeta de datos: `%APPDATA%\Kantor` (`settings.json`, `library-index.json`, `kantor.db`). La variable de entorno
  **`KANTOR_DATA_DIR`** la reemplaza (`Kantor.Core/Settings/AppPaths`): sirve para probar sin tocar los datos
  reales. **Trampa**: `Environment.GetFolderPath(ApplicationData)` ignora la variable `APPDATA`; cambiarla no aísla nada.
- **Trampa de pruebas de interfaz**: con `SendKeys` las teclas van a la ventana en primer plano, que puede no ser
  Kantor (llegaron a otras aplicaciones). Las pruebas de pantalla se hacen con UI Automation (`InvokePattern`,
  `ValuePattern`), que no necesita foco ni envía teclas.

### Código
- Core: `Kantor.Core/Data/` (`KantorStore`, modelos, `SongIdentity`, `ScoreRanking`); textos es/en en
  `Menu/PlayersTexts.cs`. App: `Players/PlayersView` (pestañas JUGADORES / SESIÓN / HISTORIAL),
  `Players/SessionPromptView`, `Pixel/PixelUi` (piezas de interfaz por código). Menú: `CANTAR · JUGADORES · OPCIONES
  · SALIR`; botón "Jugadores…" en la barra; la sesión activa se ve en el menú (arriba a la derecha).
- Abrir Jugadores, como Opciones, detiene la canción. Si la base no abre, la aplicación sigue: se canta con los
  micrófonos abiertos y sin historial (se avisa).

### Comprobado
- 40+ tests de Core (restricciones, transacciones atómicas, sesión automática y manual, duplicados, empates,
  archivados, cancelaciones, reinicio y recuperación, nueva sesión con otra abierta).
- **App real, flujo completo** con una canción de 3 s y datos aislados: Espacio sin jugadores → selección →
  crear "Ana" → elegir P1 → cantar → HUD y resultados con su nombre → cierre forzado → reabrir: ofrece la sesión
  con 1 canción → CONTINUAR restaura P1 = Ana → canción interrumpida (Opciones a mitad) no suma → canción completa
  suma → historial por sesión muestra ambas.
- **Sin comprobar a mano**: puntajes reales cantando (la prueba fue en silencio: 0 puntos), dos o más jugadores
  a la vez, INICIAR NUEVA desde el aviso (cubierto por tests de Core), archivar con la pantalla (idem), la
  y el historial por jugador en pantalla.

## 8 bis. Comentarios humorísticos del resultado

- **Bancos**: 1550 frases en `src/Kantor.Core/Humor/Texts/humor.es.json` y `humor.en.json` (recursos embebidos;
  `{ "id": "texto" }`). El inglés es el canónico; el español es adaptación (voseo), no traducción literal. Las
  fuentes originales (`design-assets/`, ignorada por git) no se usan en tiempo de ejecución.
- **IDs estables** independientes del idioma: `<categoría>_<NNN>` (`disaster_001`…`random_100`).
- **Categorías**: 0–1999 disaster · 2000–3999 trying · 4000–5999 technically · 6000–7999 good · 8000–8999
  suspicious · 9000–9499 superstar · 9500–9999 mythical (200 c/u) · 10000 divine (50) · random (100, solo easter egg).
- **Selección** (`CommentSelector`): una bolsa mezclada por categoría (sin repetir hasta agotarla; al remezclar,
  el primero no repite el último). 10000 → solo `divine`, prioridad absoluta. 0–9999 → 3 % (configurable,
  `UserSettings.NonsenseProbability`) de reemplazar el comentario por uno de `random`, nunca en dos resultados
  seguidos. Azar inyectable (`IRandomSource`) para tests deterministas.
- **UI**: al terminar la canción de forma natural se sortea **una vez** y se muestra una tarjeta con el puntaje
  y el comentario; cambiar el idioma (selector "Comentarios", `UserSettings.CommentLanguage`) solo cambia el
  texto del mismo ID. Se oculta al reproducir, detener o cargar otra canción.
- **Trampa**: los archivos `*.es.json` en un `.csproj` se toman como recursos satélite de cultura; por eso
  `WithCulture="false"` en el `EmbeddedResource`.
- Sin comprobar a mano: aspecto de la tarjeta y cambio de idioma en vivo (no hay prueba de UI).

## 9. Siguientes pasos

1. Prueba del ejecutable distribuible en una PC limpia (ver `docs/DISTRIBUCION.md`) y firma del `.exe`.
2. Empaquetado MSIX y publicación en Microsoft Store (ver `docs/DISTRIBUCION.md`).
3. ~~Prueba con voz real del feedback por frase y de la señal de onda~~ (probados por el usuario).
4. Medición de latencia: separar la parte de salida de la de entrada, el desfase de arranque (§6 bis) y
   calibrar la compuerta de nivel con el micrófono.

Decisiones abiertas: definición final de "puntos de competición" y "efectividad", y umbral de paridad
con WorldParty.

**Licencia (decidida): GPL-3.0-or-later.** Archivo `LICENSE` en la raíz y aviso en `README.md`.
- WorldParty: GPL-3.0-or-later (verificado en `LICENSE` y en la cabecera de `src/base/UMain.pas`).
- USDX: GPL-2.0-or-later (el `LICENSE` es el texto v2, pero las cabeceras de fuente dicen "or any later
  version", p. ej. `src/base/UMain.pas`). Compatible con GPL-3.0-or-later. Verificado en una muestra, no
  archivo por archivo: si algún día se reutiliza un archivo concreto, mirar su cabecera.
- **Hasta hoy no se copió código de WorldParty ni de USDX.** El scoring es implementación propia, tomando
  WorldParty solo como referencia de comportamiento. Reutilizar código concreto exige verificar la
  cabecera del archivo, dar atribución y conservar sus avisos de copyright.
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

- **Pantalla completa (F11)**: ventana sin borde dimensionada al monitor entero y `Topmost` (maximizar una ventana
  WPF sin borde solo cubre el área de trabajo y deja la barra de tareas). `NativeMonitor` consulta el monitor
  (P/Invoke, en unidades independientes del dispositivo); al salir se restauran posición, tamaño, estilo y estado.
  Comprobado: cubre 1920×1080 (área de trabajo 1920×1032) y restaura 1000×600 en (100, 100).
- **Barra técnica** (entrada, motor, estado): oculta por defecto; F9 la muestra o la oculta; un error al abrir
  una canción la muestra sola.
- **Barra de título propia** (`MainWindow`): `WindowChrome` (conserva redimensionar, ajustar a bordes y sombra de
  Windows) + barra de 38 px con logo, título de la ventana y botones pantalla completa / minimizar / maximizar /
  cerrar (glifos Segoe MDL2 Assets). En pantalla completa se oculta junto con la zona de arrastre. Maximizada, la
  ventana se pasa 8 px de cada borde: se compensa con un margen igual a lo que sobra del área de trabajo
  (`NativeMonitor.WorkAreaOf`). Comprobado: maximizada = 1920×1032 (área de trabajo); pantalla completa desde
  maximizada = 1920×1080.
- Biblioteca: el elemento seleccionado se destaca con borde dorado grueso y sombra dorada; el desplazamiento es
  por pixel y la fila activa se trae siempre completa a la vista (`BringIntoView`) al navegar con el teclado, con
  margen inferior para que no la tape la zona de ayuda. La barra de desplazamiento es propia
  (`Pixel/PixelScrollTrack`): el `Track` de WPF fuerza un pulgar de ~8 px en listas largas y no respeta `MinHeight`.
- **Rendimiento del escenario** (medido con `30 Seconds To Mars - Attack`, 12 s de canción, 1920×1080): el video
  no es la causa de los tirones (con y sin video daba lo mismo). Construir el dibujo costaba 3,8 ms por cuadro;
  con rectángulos en vez de polígonos, geometrías congeladas en caché (marcos, contorno de la letra) y texto
  formateado reutilizado bajó a ~1,0 ms; los intervalos mayores a 34 ms pasaron de 5 % a ~0,2 %. Quedan ~18 % de
  cuadros a 33 ms (≈52 fps medios) sin explicar; el proceso usa ~120 % de un núcleo (audio, captura y detección
  de tono incluidos). Notas y letra van directo sobre el video, sin recuadro (el contorno oscuro las mantiene
  legibles).
- **Rendimiento del escenario, causa real**: el contorno de la letra como trazo de geometría de texto. Con
  `Twenty One Pilots - Heathens` el escenario bajaba a **22 fps** (sin letra: 81 fps); NO eran las notas, el HUD ni
  el video. Arreglos: contorno con copias desplazadas del texto (4 en diagonal; con 8 perdía cuadros), colores del
  texto re-aplicados solo cuando cambia la cantidad de notas cantadas (re-aplicarlos re-maqueta el texto) y la
  letra en Jersey 15 (más grande por ser angosta). Medido con video, 12 s, 1920×1080, pantalla de 120 Hz: 60 fps
  sin cuadros >20 ms (con 8 copias: 59 fps y 3 %; antes: 22 fps). CPU del proceso 91 % de un núcleo.
  Herramienta de medición: arnés en el scratchpad que registra `CompositionTarget.Rendering` durante la canción.
- **Indicador "ahora te toca"** (letra): línea fina de 2 px bajo la frase por cantar. Hasta 2 s antes de que
  empiece, un marcador (cuadradito dorado) llega desde la izquierda y toca el inicio de la línea justo en el beat de
  la primera sílaba; ya cantando, la misma línea se llena en dorado con el avance de la frase. Reglas puras y
  probadas en `Kantor.Core/Songs/LyricCue.cs` (ventana, avance, frase que viene). Sin comprobar a mano: el ritmo del
  marcador con canciones de frases pegadas y de dúos.
- **Indicador "ahora te toca" (versión final)**: un único indicador, siempre bajo la frase de ARRIBA. Arriba va la
  frase que se canta y, al terminar (0,35 s de margen), la que viene; abajo, la siguiente
  (`LyricCue.DisplayIndex`). El relleno (letra en oro recortada + línea fina) sigue el tiempo real de cada sílaba:
  entera al terminar su nota, proporcional dentro de ella y quieta en las pausas (`LyricCue.FillX`, con tramos de
  cada sílaba medidos con `BuildHighlightGeometry`). Reemplaza el cambio de colores por sílaba (que obligaba a
  re-maquetar el texto) por un recorte, que además es más barato.

### Escenario: fidelidad con WorldParty y fin anticipado
- **Grosor por dificultad**: nota y barra cantada ocupan (2·tolerancia + 1) filas × 0,8 (Fácil 5, Medio 3, Difícil 1),
  con mínimo 14 px y tope 30 % del alto del carril. `StageControl.NoteTolerance` se fija en `CreateScorers`.
  Sin comprobar a mano: el grosor final con dos jugadores en Fácil.
- **Tarjeta del jugador**: hasta 340 px de ancho; nombre y puntaje se achican para entrar (el puntaje llega a
  10.000 y el porcentaje a 100). Nombre 18–32 px, puntaje 30–54 px.
- **TERMINAR**: cuando suena la música pero ya no queda letra (`MainWindow.LyricsFinished`: posición > fin de la
  última línea de cualquier voz), aparece el botón TERMINAR en los controles del mouse y se queda visible. Detiene la
  canción, la da por cantada (se guarda en la partida como un final natural) y abre los resultados.

### Resultados: todos juntos (reemplaza la ceremonia con clasificación)
- Con 2–4 jugadores se ven todos a la vez: 2 en dos columnas, 3 en una fila (personaje arriba, tarjeta debajo),
  4 en grilla de 2×2 (`PanelMetrics.For`, `ResultsView.Show`). Entran animados de a uno, del último puesto al
  ganador (2,4 s entre cada uno); Enter/Espacio completa todo. Sin animar (cambio de idioma) quedan finales.
- Se eliminó la ceremonia (clasificación lateral, presentación uno por uno, `ResultsCeremony` y sus tests).
  `ResultsInputGate` conserva las acciones Advance/SkipCeremony sin uso (candidatas a limpiar).
- Sin comprobar a mano: el aspecto con 2, 3 y 4 jugadores (no hay forma automática de llegar a resultados sin cantar
  la canción entera) y la proporción de avatares/tarjeta en cada disposición.

### Partida desde la lista de canciones + clasificación de la partida
- Botón **PARTIDA** (y **F4**) en la lista de canciones: abre la pestaña Partida (agregar/quitar jugadores, nueva o
  cerrar partida) y **LISTO/VOLVER/Esc** devuelve a la lista (`MainWindow._playersFromSongList`).
- **Clasificación de la partida** (pestaña Partida, solo con partida abierta): por jugador, puesto, puntos totales,
  promedio, mejor puntaje, canciones y victorias. `Kantor.Core/Data/SessionStats.cs` (con tests): orden por total
  (luego promedio, luego nombre); empates comparten puesto; una victoria = puntaje más alto en una canción con 2 o más
  jugadores (el empate cuenta para todos). **No** es la fórmula de "puntos de competición" de los rankings globales,
  que sigue sin definirse.
- Comprobado en la app real (datos aislados): el botón abre la partida y vuelve a la lista; sin canciones se ve "Todavía
  no se cantó…". Sin comprobar a mano: las líneas de clasificación con canciones cantadas (solo cubierto por tests).
- Diagnóstico: `kantor.log` (`Diag.cs`) con marcas de arranque/carga y vigilante de interfaz colgada.

### Rankings: reglas definidas (versión 1) — `Kantor.Core/Data/RankingStats.cs`
Reemplaza a `SessionStats`. Tres métricas separadas (spec §4):
- **Puntos musicales**: el puntaje de la canción (0–10.000), sin tocar.
- **Puntos de competición**: por canción de 2 o más jugadores, `N − puesto` (con 3: 2, 1, 0). **El último no suma.**
  Empates comparten puesto y puntos; si empatan en último, ninguno suma; si empatan todos, todos suman `N − 1`.
  Cantar solo no da puntos de competición.
- **Efectividad**: promedio de puntos musicales sobre 10.000, en %, de todas sus canciones (solo o en competencia).
- Orden: competición, efectividad, puntos totales, nombre. Puesto compartido con igual competición y promedio.
- Alcance: ranking de la partida (pestaña Partida y Historial › Por partida) y **ranking global** (Historial ›
  Ranking global, todo el historial, todos los tiempos). Sin filtros por dificultad ni mínimo de canciones (no
  pedidos). `KantorStore.GetAllHistory()` alimenta el global. Si las reglas cambian, subir `RankingStats.Version`.
- Sin comprobar a mano: el aspecto de las líneas con datos reales (cubierto por tests de las reglas).

### Voz por micrófono en duetos, mantenimiento del historial y filtros del ranking
- **Voz por micrófono (duetos)**: en "¿Quién canta?", si la canción tiene 2 o más voces, cada tarjeta de micrófono
  trae un select de voz ("Voz 1", "Voz 2", con su nombre si lo tiene). Por omisión, el reparto automático
  (`VoiceAssignment.VoiceFor`). Es por canción: se reinicia al cargar otra (`MainWindow._voiceChoice`). Cambiarla
  recrea los puntajes (a cero, antes de cantar). Sin comprobar a mano: requiere un dueto y micrófonos reales.
- **Política de datos** (decidida por mí, ajustable):
  - **Borrar partida**: solo partidas **cerradas** (la abierta se cierra primero), con confirmación de dos pasos.
    Borra sus interpretaciones y resultados; los jugadores no se tocan (`KantorStore.DeleteSession`).
  - **Exportar**: CSV UTF-8 con BOM, una fila por resultado (partida, fechas ISO, canción, dificultad, jugador, mic,
    puntaje, posición, puntos de competición): de una partida o de todo (`HistoryExport`).
  - **Respaldar**: copia consistente de la base con `VACUUM INTO` a un archivo nuevo elegido por el usuario
    (`KantorStore.BackupTo`; no sobrescribe). **Restaurar**: manual, con Kantor cerrado, reemplazando `kantor.db`.
    No hay respaldo automático ni restauración desde la interfaz.
  - Todo en Jugadores › Historial (más el botón borrar en "Por partida").
- **Filtros del ranking global**: dificultad (Todas/Fácil/Medio/Difícil) y mínimo de canciones (1, 2, 3, 5, 10);
  el mínimo se aplica antes de calcular puestos (`RankingStats.Compute(..., minSongs)`).
- Tests nuevos: `HistoryMaintenanceTests` (borrado, respaldo, CSV, mínimo de canciones). 439 pasan, 2 omitidos.

### Cola de canciones
- **Qué hace**: lista ordenada de canciones por cantar. En la lista de canciones: botón **+ COLA** (F5) agrega la
  resaltada (se puede repetir la misma), botón **COLA (n)** (F6) abre la pantalla de la cola: **CANTAR YA** (la saca
  y la carga), **SUBIR**, **BAJAR**, **QUITAR**; Esc/VOLVER regresa a la lista. Máximo 100 canciones.
- **Siguiente**: al terminar una canción, la pantalla de resultados muestra **SIGUIENTE: <título>** si la cola no
  está vacía; la saca de la cola y la carga (después, como siempre, gate de partida y "¿quién canta?").
- **Persistencia**: con partida abierta, la cola es de la partida (tabla `queue_item`, esquema **v2**, migración
  automática; sobrevive al cierre de Kantor y se borra junto con la partida). Cantando suelto o sin partida abierta,
  vive solo en memoria. No se asigna quién canta por adelantado.
- **Código**: `Core/Data/SongQueue.cs` (operaciones puras), `KantorStore.GetQueue/SetQueue`, `Core/Menu/QueueTexts.cs`,
  `App/Queue/QueueView`, integración en `MainWindow` (sección "Cola de canciones") y `ResultsView.SetNext`.
- Comprobado: agregar, contador, pantalla de la cola (captura). Sin comprobar a mano: SIGUIENTE al terminar una
  canción real, reordenar con el teclado, persistencia tras cerrar Kantor con una partida abierta (cubierta por tests).

## 8 undecies. Control remoto (Avyn) — contrato v3 implementado

Contrato cerrado y acordado con el desarrollo de Avyn: **`docs/AVYN_CONTRATO.md`** (es la referencia; este apartado
solo recoge cómo quedó en el código y lo que cuesta cambiarlo).

### Qué hay
- **Pipe** `Kantor.ControlPlane.v1` (`Core/Remote/KantorIpcServer`): `maxInstances = 1`, `PipeOptions.CurrentUserOnly`,
  marco de 4 bytes LE + JSON, máximo 1 MiB exacto (un prefijo mayor o 0 cierra la conexión). **Apagado por omisión**:
  Opciones › Juego › "Control remoto (Avyn)" (`UserSettings.RemoteControl`). El controlador y el avance automático
  existen siempre; solo el pipe es opcional.
- **Hilos**: lectura, `kantor.ping` y escritura corren en el hilo de E/S (el ping se contesta sin esperar al
  dispatcher); los comandos que mutan o leen la ventana corren de a uno en el dispatcher WPF; `library.*`,
  `queue.list`, `players.list`, `session.get/list/history` e `history.*` corren fuera de él (snapshot inmutable del
  índice; `KantorStore` ya tiene candado global). Un comando que espera más de 3 s para empezar responde `TIMEOUT`
  sin ejecutarse y su `commandId` queda libre.
- **Presencia**: el Agent manda `kantor.ping` cada 5 s; Kantor corta a los 15 s sin tráfico + 5 s de gracia; si el
  propio vigilante se atrasó más de 5 s (suspensión) el contador vuelve a empezar (`PipeLiveness`). Una escritura
  que no avanza en 5 s también corta. Un cliente nuevo no expulsa a uno vivo (el pipe está ocupado).
- **Estado y revisiones** (`StatePublisher`, `KantorController`): un solo `rev` que sube una vez por publicación;
  `state.changed` solo con las secciones que cambiaron. Cambios locales: un `DispatcherTimer` de 150 ms
  (`MainWindow.Remote.cs`) llama `Flush()`; después de cada mutación remota se publica **antes** de responder.
  Posición: sin hora absoluta (`positionSeconds` al serializar + `rate`); `display` se publica 250 ms después de
  que la ventana deja de moverse; el puntaje en vivo va por `kantor.score.tick` (≤ 1/s, sin `rev`).
- **Deduplicación** (`CommandDeduplicator`): 1024 entradas / 15 min, huella SHA-256 de tipo + payload canónico.
  En memoria: un reinicio la vacía (el contrato dice que las mutaciones no se reintentan tras reiniciar).
- **Ventana ↔ controlador**: `IKantorHost` (Core) implementado por `MainWindow.Remote.cs` (`HostAdapter`).
  `HostState` es una lectura de un instante. Detalles que importan:
  - `AbandonSeq` crece en `ShowMenu`, `OnStopClick`, abrir Opciones/Jugadores por su cuenta, `FailLoad` y falla de
    motor: así el elemento "en ejecución" de la cola se cancela aunque la persona lo haga desde la PC.
  - `ResultsInteractionSeq` cuenta teclas y clics locales en resultados **después** de la animación (la pulsación
    que la saltea no cuenta; el movimiento del mouse tampoco).
  - `ShowResult` es idempotente por ejecución (`_resultRunId`/`_runOutcomes`): un aviso de fin repetido no vuelve
    a sortear comentarios ni duplica el `kantor.result`.
- **Cola** (`Core/Data/QueueService`, reemplaza a `SongQueue`/`QueueItem`): elementos con id, estado
  (pendiente/en ejecución/completado/cancelado/quitado), jugadores por elemento, instantánea de artista/título/
  edición y revisión (`queueRev`). La usan la pantalla local y el IPC. "Cantar ya" y "SIGUIENTE" marcan el elemento
  en ejecución; al terminar pasa a completado (con `performanceId`) o a cancelado. Los quitados se conservan
  (estado interno) para distinguir "ya no está" de "nunca existió". Al abrir Kantor un "en ejecución" colgado pasa a
  cancelado (`RecoverInterrupted`).
- **Biblioteca**: `LibraryCatalog`/`LibrarySearch` sobre el mismo índice que la lista local (mismas reglas de
  `SongSearch`, incluida la edición). **Sin colapsar ediciones**: cada archivo es una entrada con su
  `libraryEntryId`; `songIdentity` (el SHA-256 de artista+título de siempre) las agrupa. Páginas cortadas por
  tamaño (256 KiB). Portadas: miniatura JPEG recomprimida (`WpfCoverProvider`, ≤ 96 KB, caché de 64).
- **`libraryEntryId`** (`KantorStore.SyncLibraryEntries`, tabla `library_entry`): GUID persistido por ruta
  normalizada; editar el TXT no lo cambia; un archivo movido hereda el id **solo** si su SHA-256 coincide con el de
  exactamente una entrada cuyo archivo ya no existe y ningún otro archivo nuevo comparte ese contenido. Nunca se
  reasocia por artista+título. Sin base de datos, el id sale de la ruta (`LibraryIds.FromPath`).
- **Esquema v3** (migración automática; la cola v2 se convierte con sus rutas y queda pendiente en el mismo orden):
  `library_entry`, `queue_entry`, `performance.library_entry_id/edition`, `performance_result.comment_id`.
  El comentario que se mostró se guarda con el resultado; lo anterior a la v3 queda sin comentario y **nunca** se
  regenera. El historial viejo no identifica la edición concreta (solo `songIdentity`).
- **Avance automático** (`AutoAdvanceMachine`): `UserSettings.AutoAdvance` (apagado). 10 s después de la animación
  de resultados (o al saltearla), cancelable con cualquier tecla o clic local, `autoAdvance.cancel`, cerrar
  resultados, menú, cualquier `playback.*` aceptado o apagar el ajuste. Usa los puestos del elemento o, si no
  tiene, la selección vigente de la partida; exige partida abierta (no suelto), entrada disponible, jugadores
  activos de la partida y micrófono en cada puesto, validado al armar y otra vez al disparar. La cuenta se ve en
  la pantalla de resultados (`ResultsView.SetCountdown`).
- **Pantalla**: manifiesto **PerMonitorV2** (`app.manifest`); `NativeMonitor.Describe` publica el nombre GDI del
  monitor, límites en píxeles físicos y escala de DPI. La pantalla completa se ajusta con `SetWindowPos` en
  píxeles físicos (`FitToMonitor`, `Topmost`, no `Maximized`); `LocationChanged`, `DpiChanged` y
  `SystemEvents.DisplaySettingsChanged` la vuelven a ajustar al monitor donde quedó. Maximizada y movida a otro
  monitor, se la vuelve a maximizar. El título de la ventana nunca queda vacío (`Kantor` o `Kantor — artista —
  título`).
- **Escalado pixel art**: `Pixel/PixelViewbox` reemplaza a `Viewbox` en todas las pantallas: cuantiza el factor a
  múltiplos de 0,25 píxeles físicos por unidad de diseño (así 4 unidades son siempre píxeles enteros) con el DPI
  del monitor actual, y centra en píxeles físicos. `UseLayoutRounding` activado en la ventana.

### Comprobado
- **Automático** (574 tests pasan, 2 omitidos por ser opt-in): marco (1 MiB exacto), validación de payloads,
  deduplicación, presencia (incluida la suspensión), revisiones, cola (orden, conflictos, persistencia, recuperación,
  historial en solo lectura), ids de entrada (mover, borrar, copias, reabrir, migración v2→v3), búsqueda y facetas,
  contrato completo del controlador con una ventana simulada (estados, políticas de partida, cantantes,
  cancelaciones, resultados con comentarios guardados, avance automático, display con calma, posición) y **un pipe
  real con un cliente de prueba** (apretón de manos, orden evento→respuesta, ping con la interfaz bloqueada, lecturas
  sin esperar a la interfaz, marco de más, cliente muerto que se libera, reconexión con reintento deduplicado,
  segundo cliente rechazado).
- **App real** (`KANTOR_DATA_DIR` aislado, cliente PowerShell contra el pipe de Kantor): handshake, biblioteca (6
  entradas), búsqueda, facetas, portada, jugadores, partida, cola (conflicto de revisión, "ya no está"),
  `playback.start` de un elemento → "¿Quién canta?" en pantalla → `NO_MICROPHONES` → `stop` (elemento cancelado y
  vuelta al menú), pantalla completa y `display` real (`\\.\DISPLAY3`, 1920×1080, escala 1,0), segundo cliente
  rechazado. Capturas del menú y de "¿Quién canta?" con `PixelViewbox`: sin cambios visibles a 100 %.

### Sin comprobar (necesita hardware o manos)
- **Dos monitores** (resoluciones o escalas distintas): reajuste de pantalla completa y maximizada al mover con
  `windows.move`, `dpiScale` real, PerMonitorV2 al arrastrar entre monitores y el aspecto de la interfaz con
  escalas no enteras (125 %, 150 %, 175 %). `PixelViewbox` solo se miró a 100 %.
- Cantar una canción entera con el Agent real: `confirmSingers` con micrófonos creados, avance automático de
  verdad (la cuenta en pantalla, la próxima canción sola), `kantor.result` con puntajes reales.
- Suspensión/hibernación con el Agent conectado; Windows puede rechazar `windows.focus` a una ventana que no tiene
  el foco (del lado de Avyn).
- Varios micrófonos a la vez (ya estaba sin comprobar).

### Limitaciones conocidas
- `Song.voices` del borrador **no se publica**: el índice de la biblioteca no cuenta voces (haría falta parsear el
  TXT completo). Tampoco hay duración fuera de la canción cargada.
- `slots.maxSlots` es la **cantidad** de micrófonos creados; como se pueden quitar dejando huecos (M1 y M3), se
  agregó `slots.availableSlots` (los puestos reales); la validación usa esa lista.
- La cola persistida de una partida se muestra "no disponible" hasta que el catálogo termina de armarse al
  arrancar (fracción de segundo); no se reasigna nada por eso.
- Con la base cerrada o rota hay catálogo, cola en memoria y control remoto, pero sin partidas ni historial.
- `session.start/close`, `players.archive` y `session.removePlayer` devuelven `BUSY` mientras se canta (cambiarían
  una interpretación en curso). Es una regla nuestra: el contrato no la enumeraba.
- Una canción abierta con Ctrl+O desde fuera de la biblioteca recibe una entrada de biblioteca propia al guardarse
  (`EnsureLibraryEntry`), pero no figura en `library.search` mientras no esté en una carpeta configurada.

## 8 duodecies. Acceso de invitados por LAN

Guía de uso y seguridad: **`docs/INVITADOS.md`**. Aquí, lo que hay que saber para tocar el código.

- **Qué es**: servidor Kestrel (`Kantor.Core/Guest/`, `FrameworkReference Microsoft.AspNetCore.App`) para que los
  celulares busquen una canción, elijan cantantes **ya existentes** y la agreguen a la cola. Nada más. Apagado por
  omisión **y siempre al arrancar** (no hay ajuste que lo deje encendido); el token se emite al activar y se revoca
  al apagar o cerrar. Dependencia nueva: QRCoder 1.6.0 (MIT), solo la matriz de módulos (`GuestQr`).
- **Piezas**: `GuestAccess` (token 128 bits, comparación en tiempo constante), `GuestRateLimiter`, `GuestNetwork`
  (interfaces IPv4 privadas, activas y no virtuales; elección), `GuestService` (búsqueda, jugadores, agregado
  idempotente), `GuestWebServer` (rutas, cabeceras, límites), `KantorController.Guest.cs`
  (`GuestRosterSnapshot`/`GuestEnqueue`: **reutilizan `ValidatePlayers` y `QueueService.Add`** de `queue.add`; corren
  en el hilo de la interfaz y publican a Avyn con `Flush()`).
- **Reglas de cantantes**: máximo = micrófonos creados (no se sabe si la canción es un dueto: el índice no cuenta
  voces); el cantante *i* ocupa el *i*-ésimo puesto con micrófono; con partida abierta solo sus jugadores, sin
  partida (o cantando suelto) cualquier activo. Sin micrófonos no se agrega (`NO_MICROPHONES`).
- **Cambio en el flujo local**: `StartQueueItem` (CANTAR YA / SIGUIENTE) ahora carga como selección los cantantes
  del elemento (`ApplyQueueSlots`) si siguen siendo válidos. Antes solo los usaban Avyn y el avance automático.
- **Interfaz**: `Guest/GuestCard` (QR chico, en el menú y en la lista; apagado es un botón «INVITADOS · QR»),
  `Guest/GuestAccessView` (pantalla grande, interruptor), `Guest/QrView`, `MainWindow.Guest.cs`. Red a usar:
  Opciones › Juego › «Red para invitados» (`UserSettings.GuestInterface`). Textos en `Menu/GuestTexts.cs`.
- **Página**: `Guest/Web/` (HTML, CSS, JS sin frameworks, embebidos con `LogicalName` fijo; el servidor sirve solo esa
  lista). Valores de `Theme.xaml`/`PixelFrame`/`SongListView.xaml` en variables CSS; Jersey 15 embebida desde
  `Kantor.App/Assets/Fonts`. El logo (`logo.png`, 640 px) es una versión reducida del de la app.
- **Trampa**: `HttpListener` exige permisos de administrador/urlacl; por eso Kestrel. **Trampa**: las imágenes llevan
  el token, así que la página las pide con `fetch` y las muestra como blob (un `<img src>` no puede mandar el header).
- **Comprobado**: 130+ tests nuevos (`GuestTests.cs`, con Kestrel real en loopback y el controlador real: estado
  apagado, arranque/parada, revocación, token, búsqueda con acentos, paginación, vacíos, canciones/jugadores
  inexistentes, reglas de cantantes, agregado a la cola con los puestos, duplicados, concurrencia, límites de pedidos,
  rutas prohibidas, sin acceso a archivos, errores sin detalles, `Host`/`Origin`, tamaño de cuerpo, QR). La app real
  arranca, escucha **solo** en la IP de la LAN (loopback rechazado), sirve la página y contesta 401/403 donde
  corresponde; el QR se ve en el menú. La página, en Chrome headless con viewport de celular (vertical y horizontal):
  búsqueda, selección de cantantes, «¡EN COLA!» y vuelta a la lista.
- **Sin comprobar**: un celular real (escaneo del QR con la cámara, el teclado, el cambio de red), el Firewall de
  Windows y Wi-Fi con aislamiento de clientes, varias personas a la vez, la tarjeta de la lista de canciones a
  simple vista, la pantalla grande del QR, Opciones › Juego › Red para invitados y el cambio de red en caliente,
  un dueto, y la lista de canciones con la biblioteca real.
- **Crear jugadores desde el celular** (ampliación): pantalla «+ NUEVO JUGADOR» (nombre + galería de avatares de fábrica)
  y `POST /api/players` (solo crea) + `GET /api/avatars`. Reutiliza `KantorStore.CreatePlayer` (mismas reglas que la PC:
  1–24 caracteres, único entre activos sin distinguir mayúsculas) y `CharacterCatalog.Factory`/`Fallback`; se suman
  solo: sin caracteres de control, avatar únicamente de fábrica (los del usuario no se ofrecen ni se aceptan) y
  validación en el servidor (`KantorController.GuestCreatePlayer`, en el hilo de la interfaz).
  - **Partida**: con una partida abierta y sin cantar suelto, el jugador entra a ella (`AddPlayerToSession`), igual que
    los creados desde «¿Quién canta?» en la PC; sin eso no podría elegirse (la lista de invitados es la de la partida).
    No toca la selección de micrófonos.
  - **Nuevo `IKantorHost.PlayersAdded()`**: refresca la caché de nombres y la pantalla de jugadores **sin** llamar a
    `DataChanged`, que recrea los puntajes (`CreateScorers`) y reiniciaría una canción en curso. (`players.create` de
    Avyn sigue usando `DataChanged`: no se tocó el contrato.)
  - **Idempotencia**: por `requestId` (mismo mecanismo que la cola, con claves separadas `p:`/`q:`); mismo id y mismo
    contenido devuelve el mismo jugador (`duplicate`), mismo id con otro contenido es `REQUEST_REUSED`; un pedido
    rechazado no se recuerda. Dos pedidos independientes con el mismo nombre siguen la regla normal (`NAME_TAKEN`).
  - **Límites**: 5 creaciones por cliente cada 10 min y 40 en total por activación (`GuestServerOptions`).
  - **Página**: tercera pantalla (biblioteca → cantantes → nuevo jugador) con historial del navegador (el botón atrás
    retrocede una pantalla); al crear vuelve a «¿Quién canta?» con el jugador ya elegido (si el cupo de micrófonos
    estaba completo, ocupa el lugar del último elegido). En pantallas bajas en horizontal, el botón de crear queda al
    final del formulario en vez de fijo.
  - **Sin comprobar**: teclado virtual real, la galería con el dedo, y la aparición del jugador en la pantalla de
    Jugadores de la PC mientras está abierta (cubierto por tests solo hasta `PlayersAdded`).

### Ediciones: local y Microsoft Store (avatares)
- `Edition=Local` (por omisión) trae los 45 avatares; `Edition=Store` define `STORE_EDITION` y quita los de
  `Kantor.Core/Characters/store-excluded.txt` (ids separados por `;` o saltos de línea, un subconjunto de los locales).
  `CharacterEdition` lee la lista (recurso embebido), `CharacterCatalog.Factory` se arma sin ellos y la App ni siquiera
  embebe sus PNG (`Resource Remove` en `Kantor.App.csproj`). Todo lo que usa la galería (PC, web de invitados, validación
  de la creación de jugadores) sale de `Factory`, así que sigue coherente. `FactoryCount` es el total del repositorio.
- `scripts/publish.ps1 -Edition Store` → `dist\Kantor-<v>-store-win-x64`; sin instalador. **Trampa**: MSBuild no regenera los
  recursos de WPF al cambiar solo la lista de recursos: el script borra `obj` antes y después.
- Un jugador con un avatar que la Store no trae se muestra con el genérico (`GetOrDefault`); el id guardado no se toca.
- Tests (`CharacterEditionTests`): análisis de la lista, subconjunto y orden, caída del genérico, y que el archivo real solo
  nombre avatares de fábrica. Se corrieron en las dos ediciones. **Excluidos de la Store (decididos el 16/10/2026, por parecido con personajes ajenos): `avatar_025`, `avatar_029`, `avatar_030` y `avatar_031`; la Store trae 41 de los 45.**

### Paquete MSIX y créditos
- `publish.ps1 -Edition Store -Msix` → `dist\Kantor-<v>-store.msix` (ver `docs/DISTRIBUCION.md`); manifiesto e identidad en
  `installer/store/`. **Trampa**: `makeappx` exige los logos con el nombre exacto del manifiesto si no hay `resources.pri`
  (por eso hay archivos sin calificador además de los `.scale-N`); la variable `$msix` choca con el parámetro `-Msix`.
- Opciones › **CRÉDITOS** (`OptionsView.Section.Credits`, textos `credits.*` en `OptionsTexts`): versión y edición, autoría,
  inspiración (UltraStar Deluxe / WorldParty, sin código copiado), arte y marca, tecnología de terceros y canciones. Las seis
  pestañas entran con `MinWidth=160`. Sin controles enfocables: ↑↓ desplazan, como la guía.

## 8 terdecies. Música de la interfaz

- **Qué suena**: «8Bit Title Screen» (Joth, CC0) en el menú y la navegación, y «Heroic Loop» (Zane Little Music, CC0) en resultados;
  detalles de origen, licencias y conversiones en `Assets/Music/README.md` y `THIRD-PARTY-NOTICES.md`. Volumen en Opciones › Audio
  › **Música de interfaz** (0–100 % en pasos de 10; 0 = apagada; por omisión 20 %; `UserSettings.MusicVolumePercent`, se aplica en
  vivo y se guarda).
- **Arquitectura**: `Kantor.Core/Music/MusicCoordinator` es la única fuente de verdad. Recibe la escena (`MusicScene`: None,
  Navigation, Library, Song, Results), el volumen y el tiempo, y mueve un nivel de fundido por pista (salida 500 ms, entrada 400 ms;
  volumen final = fundido × volumen). Habla con `IMusicOutput`/`IMusicChannel` (una salida por pista): la implementación de la App
  (`Audio/UiMusic.cs`) es `WaveOutEvent` sobre el dispositivo predeterminado, como la vista previa de canciones, con el audio
  decodificado en memoria (16 bits) y loop sin costura. **No toca** el motor de la canción (WASAPI/ASIO), la captura, el puntaje ni
  los relojes.
- **Escena** (`MainWindow.Music.cs`, `DesiredMusicScene`, un temporizador de 33 ms): resultados visibles → Results; canción sonando o
  en pausa → Song (nada suena, tampoco en pausa); lista de canciones → Library (**callada**, para que se oigan las vistas previas;
  decisión posterior a la especificación inicial); el resto → Navigation. La música del menú **sigue donde quedó** al volver
  (la salida se cierra al silenciarse y se conserva la posición del loop). Volver a Navigation desde una canción espera 150 ms
  para no parpadear entre el fin de la canción y los resultados. Cada entrada a resultados parte del principio, salvo que la
  melodía siga sonando (entrada y salida muy rápidas: continúa sin saltos).
- **Fallos**: archivo ausente o ilegible → esa pista no suena y el resto sigue; salida que no abre o se cae (dispositivo
  desconectado) → se reintenta cada 2 s y se retoma sin reiniciar el loop; cierre → se detiene y libera todo sin fundido.
- **Distribución**: los archivos son `Content` (se copian a `Assets\Music` en compilación y publicación; los incluyen el zip, el
  instalador y el MSIX) y se leen desde `AppContext.BaseDirectory`.
- **Comprobado**: 37 tests nuevos (`MusicCoordinatorTests`: estados, fundidos, volumen 0 y 1–100, cambios rápidos, fallos,
  persistencia, cierre) con canales falsos y tiempo manual; en la app real, con el medidor de sesión de audio de Windows, el
  proceso suena en el menú (pico ≈ 0,13–0,16 con 20 %), calla en la lista de canciones y vuelve al volver al menú y en Opciones.
  **Sin comprobar a oído / con manos**: el aspecto de los fundidos, cantar una canción real y pasar a resultados (Heroic Loop), el
  cambio de dispositivo de salida en caliente y el volumen en vivo.

### TERMINAR siempre disponible
- El botón **TERMINAR** del escenario (junto a PAUSA y MENÚ) ahora aparece **con cualquier canción en curso, sonando o en pausa**, no solo al acabarse la letra; sigue saliendo con el mouse en movimiento y queda fijo en pausa. La tecla **Fin** hace lo mismo (solo con una canción en curso; con los resultados abiertos conserva su función de saltear la ceremonia). Va a resultados con el puntaje que lleva y se guarda como cualquier final (`MainWindow.FinishSong`). **Escape**: sonando → pausa; **en pausa → termina y va a resultados** (antes volvía al menú descartando la canción); sin canción en curso, vuelve al menú. Desde resultados, Esc o CERRAR vuelven al menú y OTRA VEZ la repite. Sin comprobar a mano (hace falta un micrófono para empezar una canción).
- **Salir antes de que se acabe la letra pregunta** (`Stage/FinishPromptView`): TERMINAR, Fin o Esc en pausa abren «¿TERMINAR LA CANCIÓN?» con GUARDAR Y VER RESULTADOS / VER SIN GUARDAR / SEGUIR CANTANDO (Esc = seguir). Mientras se decide, la canción queda en pausa; «seguir» la reanuda si sonaba. «Sin guardar» marca la ejecución como ya guardada (`_runSaved`), así que `SavePerformance` no escribe nada, y se avisa en la barra técnica; Avyn recibe el resultado con `saved=false`. Sin partida abierta solo se ofrece ver o seguir (de todos modos no se guarda). Si la letra ya terminó (`LyricsOver`), termina directo y guarda, como antes. Sin comprobar a mano ni a la vista (hace falta un micrófono para empezar una canción).
