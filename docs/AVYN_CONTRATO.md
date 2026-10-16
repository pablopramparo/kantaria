# Contrato Kantor ↔ Avyn — v3 (cerrada e implementada)

Pipe `Kantor.ControlPlane.v1`, mismo patrón que Auren. Sustituye a la v2 y a los borradores anteriores.
Implementado en `Kantor.Core/Remote` (servidor, controlador y protocolo) y `Kantor.App` (ventana y pantallas). Los
esquemas JSON los mantiene cada repo a mano (sin código compartido).
Convención: el concepto es `session`; en pantalla, en español, es **"Partida"** (y "cantar suelto" = `solo`).

## 0. Decisiones cerradas

| Tema | Decisión |
|---|---|
| Identidad de canciones | `libraryEntryId` (entrada concreta, con su edición) y `songIdentity` (agrupa para estadísticas e historial). **No hay `songId`.** Las ediciones no se colapsan en el IPC. |
| Cantantes desde Avyn | `confirmSingers` permitido con puestos asignados y estado válido; sin ajuste local extra. |
| Avance automático | Apagado por omisión; 10 s tras la animación de resultados; cuenta visible; cancelable; `settings.setAutoAdvance` desde Avyn (Free). |
| Monitores y ventana | `windows.move` / `windows.focus` de Avyn; **no existen en el IPC**. El Agent resuelve el `id` de monitor (EDID) a partir de `deviceName`. |
| Origen | Lo genera el Agent desde la identidad autenticada; Android no lo define. |
| Pipe | Se recupera de cortes y reinicios del Agent; un cliente nuevo no expulsa a uno vivo. |

## 1. Transporte

- Kantor es servidor; el Agent es cliente y reintenta cada 3 s. `maxInstances = 1`, ACL solo al SID del usuario
  actual, sin puertos de red. Apagado por omisión: se habilita en Opciones › Control remoto (Avyn).
- **Marco**: 4 bytes little-endian con el largo del JSON UTF-8 que sigue. **Máximo 1 048 576 bytes** (1 MiB exacto,
  sin contar el prefijo; 1 048 577 es inválido). Prefijo 0 o mayor al máximo ⇒ error de protocolo: no se puede
  resincronizar, **se cierra el pipe**. Kantor nunca emite un marco mayor: pagina antes.
- Mensajes: `{id, type, commandId?, origin?, payload}` → `{id, ok, rev, payload | error}`; eventos `{type, rev?, payload}`.

### 1.1 Presencia: `kantor.ping`

- El Agent envía `kantor.ping` cada **5 s**; Kantor responde `ok`. Es una solicitud normal (`id`, sin `commandId`).
- Kantor considera muerta una conexión si pasan **15 s sin ningún marco entrante** y, además, **un ciclo de gracia de
  5 s** más (peor caso 20 s). Con la conexión muerta cierra el pipe y el próximo reintento del Agent entra.
- Garantías para no expulsar clientes vivos:
  1. La lectura del pipe corre en un hilo/tarea de E/S propio. **Se registra actividad al recibir el marco, antes
     de despacharlo**, y el `ping` se responde **ahí mismo, sin pasar por el dispatcher WPF**.
  2. El vigilante usa reloj monotónico y corre cada 1 s. Si detecta que **él mismo** se atrasó (> 5 s entre
     ejecuciones: suspensión, hibernación, proceso congelado, `PowerModes.Resume`), reinicia el contador de todos los
     clientes y empieza de nuevo: un retraso propio nunca se cobra al cliente.
  3. Un cliente que sigue enviando marcos (cualquiera) nunca vence.
- **Escritura**: las escrituras son asíncronas con una cola acotada. Si una escritura no avanza en 5 s (el Agent
  dejó de leer y el búfer del pipe se llenó) la conexión se considera muerta.
- Costo de una expulsión equivocada: el Agent reconecta en ≤ 3 s, recibe `hello` + snapshot y puede reenviar con el
  mismo `commandId` lo que quedó sin respuesta (1.4). No es una pérdida de datos.

### 1.2 `origin` y `commandId`

- `origin = { clientId (≤ 64), name (≤ 48) }` lo construye el Agent con `ClientDeviceId` y `ClientDeviceName`
  autenticados (nombre normalizado a 48). Android no puede definirlo. Kantor confía en el Agent (el pipe es el límite
  de confianza) y lo usa para auditoría y para informar conflictos (`details.by`). Obligatorio en mutaciones; si
  falta o excede los límites → `INVALID_PAYLOAD`.
- `commandId` (8–64 car., `[A-Za-z0-9_-]`) obligatorio en las mutaciones de 3.9; se ignora en lecturas. El Agent
  lo transforma como `SHA-256(deviceId + ":" + commandId)` en hexadecimal (64 car.), de modo que la deduplicación es
  **por dispositivo** y un `COMMAND_ID_REUSED` solo puede ocurrir dentro de un mismo dispositivo.
- Kantor guarda hasta 1024 entradas o 15 min: `commandId → (huella, respuesta)`. Huella = SHA-256 de `type` +
  `payload` canónico (claves ordenadas; sin `id`, `origin`, `commandId`).
  - mismo `commandId` y misma huella ⇒ **misma respuesta**, sin volver a ejecutar (si el original sigue en curso,
    espera y responde igual);
  - mismo `commandId` y huella distinta ⇒ `COMMAND_ID_REUSED`; no se ejecuta ninguno.
- El caché vive en memoria: **se pierde al reiniciar Kantor** (ver 1.4).

### 1.3 Revisiones y orden de eventos

- `rev` es **un solo contador** del estado publicado. Sube en 1 por cada **publicación** (`kantor.state.changed`);
  varios cambios dentro de un mismo turno del dispatcher se agrupan en **una** publicación con todas sus secciones.
- Orden garantizado ante una mutación: primero llega `kantor.state.changed` con `rev = N` y **después** la
  respuesta con `rev = N`. Mutación sin cambios ⇒ sin evento; la respuesta lleva el `rev` actual.
- **Fuera de `rev`** (no forman parte del espejo; no producen saltos): `kantor.result` (lleva el `rev` vigente),
  `kantor.score.tick` y las respuestas de `ping`.
- Si el Agent ve `rev ≠ último + 1` ⇒ `kantor.state.get` y reemplaza el espejo.
- Concurrencia de edición, aparte de `rev`: `queueRev` (cola) y `selectionRev` (puestos); contadores en memoria,
  válidos solo dentro de un `instanceId`.

### 1.4 Arranque, reconexión y reinicio de Kantor

1. Conexión nueva ⇒ `kantor.hello` y enseguida `kantor.state` completo. **Siempre**: el Agent reemplaza el espejo.
2. `hello.instanceId` cambia en cada arranque. Si cambió, `rev`, `queueRev`, `selectionRev`, el caché de `commandId`
   y toda mutación en vuelo **dejaron de valer**.
3. **Las mutaciones no se reintentan tras un reinicio de Kantor**: las que no tuvieron respuesta quedan como
   resultado **desconocido** (1.5) y el usuario decide.
4. Misma `instanceId` (reinició el Agent): se puede reenviar la mutación sin respuesta con el mismo `commandId`.

### 1.5 Timeouts y resultado desconocido

| Salto | Límite |
|---|---|
| Android → Agent | lo decide Avyn; debe ser **mayor** que el siguiente |
| Agent → Kantor | 8 s |
| Kantor, espera en cola | un comando que lleva > 3 s sin empezar responde `TIMEOUT` **sin ejecutarse** |
| Kantor, ejecución | ≤ 5 s (lecturas pesadas 3 s); no hay trabajo mayor en el dispatcher |

- **Rechazada** = `ok:false` con código de Kantor: **no se aplicó nada** (`details.applied:false`), incluido
  `TIMEOUT`. Única excepción: `INTERNAL_ERROR` (`details.applied:"unknown"` ⇒ refrescar estado).
- **Aplicada** = `ok:true`.
- **Desconocida** = el Agent no recibió respuesta. Con la misma `instanceId` se resuelve reenviando el mismo
  `commandId`; con otra no se reintenta y se refresca el estado.

### 1.6 Compatibilidad dentro de `v1`

- **Aditivo**: campos opcionales, eventos, comandos (bajo una capacidad anunciada) y valores de enumeraciones nuevos.
  El receptor **ignora campos desconocidos** y trata un enum desconocido como "otro".
- **Incompatible ⇒ otro pipe** (`Kantor.ControlPlane.v2`); no hay negociación.
- `hello.protocolVersion` = 1 (fijo mientras el pipe sea `v1`); `hello.contractRevision` = 3 (informativo).
  Cada capacidad anuncia una versión entera; si el Agent no la soporta, no la usa.
- Errores: comando inexistente ⇒ `UNKNOWN_COMMAND`; capacidad no anunciada ⇒ `CAPABILITY_UNAVAILABLE`;
  JSON inválido o marco ilegible ⇒ `PROTOCOL_ERROR` (con `id` si se pudo leer; si no, se cierra el pipe).

## 2. Estado (`kantor.state` y `kantor.state.changed`)

`state.changed` trae **solo** las secciones que cambiaron (cada una completa).

```json
{ "type": "kantor.state", "rev": 412, "payload": {
  "instanceId": "b3c1…",
  "screen": "stage",
  "playback": { "status": "playing", "positionSeconds": 38.1, "rate": 1, "durationSeconds": 212.4,
                "libraryEntryId": "…", "songIdentity": "…", "artist": "…", "title": "…", "edition": null,
                "coverId": "…", "queueItemId": "q-7", "performanceId": "…", "difficulty": "easy", "solo": false },
  "session": { "sessionId": 12, "name": null, "startedAt": "…", "playerIds": [1,2,3] },
  "slots": { "selectionRev": 9, "maxSlots": 2,
             "items": [ { "slot": 1, "playerId": 1, "name": "Ana", "score": 4210 },
                        { "slot": 2, "playerId": null } ] },
  "queue": { "queueRev": 31, "persistent": true, "pendingCount": 3, "items": [ /* pendientes + en ejecución */ ] },
  "lastResult": { "performanceId": "…", "finishedAt": "…", "saved": true },
  "settings": { "autoAdvance": false, "language": "es", "remoteControl": true },
  "autoAdvanceCountdown": null,
  "library": { "libraryRev": 14, "scan": "idle", "entryCount": 1068, "folderCount": 2 },
  "display": { "fullscreen": true, "processId": 14212, "windowHandle": "0x00040A1C",
               "monitor": { "deviceName": "\\\\.\\DISPLAY2", "primary": false,
                            "bounds": { "x": 1920, "y": 0, "width": 1920, "height": 1080 }, "dpiScale": 1.0 } },
  "attention": [ { "code": "AUDIO_ENGINE_FALLBACK", "params": { "requested": "asio", "active": "windows" } } ] } }
```

- `screen`: `menu | library | queue | options | players | session-gate | who-plays | who-sings | loading | stage | results`.
- `playback.status`: `idle | preparing | playing | paused | finished`. `preparing` cubre la carga y las decisiones
  locales (`screen` dice cuál); `finished` = resultados en pantalla. Sin canción, los campos de canción van `null`.
- **Posición**: sin hora absoluta. `positionSeconds` es la posición **en el instante en que Kantor serializa** y `rate`
  vale 1 mientras `playing`, 0 en el resto. El Agent estampa con su reloj al recibir y extrapola
  `position + (ahora − recibido) × rate`. Se publica solo al cambiar de estado, saltar o reiniciar.
- `durationSeconds` solo existe con una canción cargada (el índice no la tiene).
- `slots.maxSlots` = micrófonos creados en Opciones (0–4). Con 0, `start`/`confirmSingers` ⇒ `NO_MICROPHONES`.
  Varios micrófonos simultáneos **no se verificaron con hardware**.
- `queue.persistent` es `false` cantando suelto o sin partida.
- `autoAdvanceCountdown`: `null` o `{ "secondsRemaining": 7.5, "itemId": "q-8" }`, medido **al serializar**; el Agent
  extrapola desde la recepción. Se publica al armarse, al cambiar `itemId` y al terminar; no por cada segundo.
- `library.scan`: `idle | scanning`. `entryCount` cuenta entradas, no canciones distintas.
- Nunca se envía audio, video, notas ni letra. `kantor.score.tick` `{ slots:[{slot,score,accuracy}] }` ≤ 1/s,
  mientras suena, **sin `rev`**; el snapshot trae el último.

### 2.1 `display` y monitores

- `monitor.deviceName` = nombre GDI (`\\.\DISPLAYn`, `MONITORINFOEX.szDevice`). El Agent lo relaciona con el `id`
  estable (EDID) de su `monitors.list`; **Kantor no genera ni conoce ese ID**.
- `bounds` en **píxeles físicos** del escritorio virtual. Para que lo sean, Kantor se declara **PerMonitorV2**
  (`app.manifest`). `dpiScale` = DPI del monitor / 96.
- `windowHandle`: HWND en hexadecimal con `0x`; `processId`: PID de Kantor. El Agent los valida entre sí.
- **Título de ventana**: `Kantor`, siempre no vacío (también en pantalla completa y maximizada).
- **Se republica `display`** (con un retardo de calma de 250 ms, para no emitir una publicación por cada píxel
  arrastrado) al cambiar de monitor, de tamaño, de DPI, de pantalla completa o la configuración de pantallas
  (`DisplaySettingsChanged`).
- **Cómo es la pantalla completa**: ventana sin borde, `Topmost`, **no** `WindowState.Maximized`, dimensionada al
  rectángulo completo del monitor donde está; al salir restaura posición, tamaño, estilo y estado previos.
- **Qué pasa al mover** (por `windows.move`, `SetWindowPos`): en pantalla completa, Kantor reajusta la ventana al
  monitor que contiene su centro y republica; maximizada, la vuelve a maximizar en el monitor de destino; normal,
  solo republica. **Recomendación al Agent**: mover con la ventana en estado normal (no maximizada) y volver a
  aplicar fullscreen/maximizar con `kantor.screen.setFullscreen` o `windows.*`. Reajustes y DPI mixto **sin probar
  con dos monitores** (ver 10).

## 3. Comandos

`rev` en toda respuesta ok. Errores en la sección 5.

### 3.1 Biblioteca (`library`)

Lecturas **fuera del dispatcher** sobre un snapshot inmutable del índice; `libraryRev` identifica el snapshot.

| Comando | Payload | Respuesta |
|---|---|---|
| `kantor.library.search` | `{ query?, songIdentity?, genre?, year?, language?, sort: "artist"\|"title", offset, limit ≤ 50 }` | `{ libraryRev, total, offset, nextOffset \| null, items }` |
| `kantor.library.get` | `{ libraryEntryId }` | `Song` |
| `kantor.library.facets` | `{}` | `{ libraryRev, genres: [{value,count}], years: […], languages: […] }` |
| `kantor.library.cover` | `{ libraryEntryId, size: 96 \| 256 }` | `{ coverId, mime: "image/jpeg", width, height, dataBase64 }` |

`Song = { libraryEntryId, songIdentity, artist, title, edition?, genre?, year?, language?, hasVideo, voices, coverId? }`.
Sin duración (el índice no la tiene).

- **Mismas entradas que la biblioteca local**: una fila por archivo, incluidas las ediciones del mismo tema. `total`
  y `facets` cuentan entradas. Misma regla de búsqueda que la lista (`SongSearch`: todas las palabras, sin
  mayúsculas ni acentos, sobre artista, título, género, año, idioma **y edición**; las que empiezan con la consulta,
  primero). `songIdentity` es un filtro exacto: devuelve **todas las ediciones** de un tema y se combina con el resto.
- Orden estable: el pedido, luego artista, título, edición y `libraryEntryId`.
- `genre`/`year`/`language`: igualdad normalizada (el año es texto en el índice; valores no numéricos se conservan).
- **Paginado por tamaño**: la página se corta en `limit` ítems o **256 KiB** de JSON, lo que ocurra primero (al
  menos 1). `nextOffset = null` ⇒ fin. Si `libraryRev` cambia entre páginas, el Agent reinicia desde 0.
- La biblioteca de IPC es el **índice vigente**: una entrada que ya no está en disco o en las carpetas configuradas
  no aparece en `search`; `get`/`cover` sobre ella ⇒ `NOT_FOUND` con `details.reason: "gone"`.
- **Portadas**: miniatura recomprimida (JPEG, borde mayor 96 o 256, ≤ 96 KB; si no entra baja la calidad).
  `coverId` = hash de ruta+fecha+tamaño **del archivo de portada de esa entrada**; la caché del Agent se indexa por
  `(libraryEntryId, coverId, size)`. Dos ediciones con la misma imagen tienen cada una su entrada de caché.
  Nunca viajan en búsquedas ni rutas.

### 3.1.1 `libraryEntryId` (estabilidad)

- Opaco, ≤ 64 car. Lo **asigna y persiste Kantor** en `kantor.db` (tabla `library_entry`: id, ruta normalizada,
  hash del contenido del TXT, primera y última vez vista); sobrevive a reinicios, a reconstruir
  `library-index.json` y a restaurar un respaldo de la base.
- **Mismo archivo ⇒ mismo id**: la clave primaria de coincidencia es la ruta completa normalizada (sin distinguir
  mayúsculas). Editar el TXT en su lugar no cambia el id.
- **Archivo movido o renombrado** (independencia de la ruta, con una sola regla): una ruta nueva hereda el id de una
  entrada existente **solo si** el SHA-256 de su TXT coincide con el de **exactamente una** entrada cuyo archivo
  ya no existe. Con 0 o 2+ candidatas se crea un id nuevo (nunca se adivina). **No se reasocia jamás por
  `songIdentity`** (artista+título), ni siquiera con una sola coincidencia. Si el TXT cambió *y* se movió, es una
  entrada nueva.
- **Desaparece** ⇒ la entrada no se borra: queda `available:false` donde figure (cola) y vuelve a estar disponible
  si el archivo reaparece en esa ruta. Las ediciones restantes no la reemplazan.
- Sin base de datos (no abrió): los ids son solo de la instancia (valen con ese `instanceId`), la cola no persiste y
  se avisa `DATABASE_UNAVAILABLE`.

### 3.2 Cola (`queue`)

`QueueItem = { itemId, libraryEntryId, songIdentity, artist, title, edition?, state:
"pending"|"running"|"completed"|"cancelled", order, available, slots: [{slot, playerId}],
addedBy: {clientId,name}?, addedAt, finishedAt?, performanceId? }`

`artist`, `title`, `edition` y `songIdentity` son una instantánea guardada en el elemento: se muestran aunque la
entrada ya no exista. `available:false` = la entrada no está en el índice vigente (no se puede iniciar; sí quitar).
**No se reemplaza por otra edición** (ver 3.1.1).

| Comando | Payload |
|---|---|
| `kantor.queue.list` | `{ sessionId?, includeFinished?: false, offset?, limit? ≤ 100 }` |
| `kantor.queue.add` | `{ libraryEntryId, position?: "end"\|"next", slots? }` |
| `kantor.queue.remove` | `{ itemId }` |
| `kantor.queue.move` | `{ itemId, beforeItemId \| null, expectedQueueRev? }` (`null` = al final) |
| `kantor.queue.setNext` | `{ itemId, expectedQueueRev? }` |
| `kantor.queue.setSlots` | `{ itemId, slots: [{slot, playerId}] }` |
| `kantor.queue.clear` | `{ confirm: true, expectedQueueRev }` (solo pendientes) |

- `list { sessionId }`: cola de esa partida en **solo lectura** (también cerrada), con terminados y cancelados. Sin
  `sessionId`: la de la partida abierta (o la de memoria si se canta suelto).
- Máximo **100 pendientes**; los terminados no cuentan. Agregar nunca toca la interpretación en curso. Agregar una
  entrada `available:false` ⇒ `ENTRY_UNAVAILABLE`.
- `running` no se puede quitar ni mover (`ITEM_RUNNING`); se cancela con `playback.stop`.
- **Conflictos**: se aplican en orden de llegada, de a uno. `expectedQueueRev` vencido ⇒ `CONFLICT` con la cola
  vigente en `details`. Sin él, la operación es relativa a `beforeItemId` y gana la última (determinista).
  `NOT_FOUND` lleva `details.reason`: `"unknown"` (nunca existió) o `"gone"` (existió y ya no está).
- Un `running` interrumpido pasa a `cancelled`; al terminar bien, a `completed` con `performanceId`.

#### 3.2.1 Entre partidas y reinicios

- Con partida abierta la cola es de la partida y **sobrevive al cierre de Kantor** (un `running` que quedó
  colgado pasa a `cancelled` al reabrir).
- Cerrar la partida conserva la cola en solo lectura; **una partida nueva empieza con la cola vacía**.
- Cantando suelto, la cola es solo de memoria y se pierde al cerrar.

### 3.3 Jugadores (`players`)

| Comando | Payload |
|---|---|
| `kantor.players.list` | `{ includeArchived?: false }` → `[{ playerId, name, characterId, archived, lastActivityAt }]` |
| `kantor.players.create` | `{ name (1–24), characterId? }` |
| `kantor.players.rename` | `{ playerId, name }` |
| `kantor.players.archive` | `{ playerId }` |
| `kantor.players.setCharacter` | `{ playerId, characterId }` |
| `kantor.characters.list` | `{}` → `[{ characterId, name, hasPortrait, hasFullBody }]` |
| `kantor.selection.set` | `{ slots: [{slot, playerId}], expectedSelectionRev? }` |

- Sin invitados anónimos: `playerId` existente, activo y, con partida abierta, miembro de ella. Un jugador, un
  puesto; `slot` ∈ 1..`maxSlots`. No cambia micrófonos ni calibración.
- `selection.set` reemplaza la selección completa. `expectedSelectionRev` vencido ⇒ `CONFLICT` con la vigente.
- **Personajes**: el catálogo trae 30 avatares de fábrica (`avatar_001` … `avatar_030`) y suma los que el usuario
  carga en la PC (`usuario-<id>`; no se crean ni se editan desde Avyn). **Los avatares no tienen nombre propio**:
  `name` repite el `characterId` y no debe mostrarse. `hasPortrait` / `hasFullBody` indican si el personaje tiene
  esa imagen. Un `characterId` desconocido (por ejemplo, un personaje borrado) se muestra con el primero del
  catálogo; el ID guardado en el jugador no se pierde. Avyn **no recibe las imágenes** en v1.
- Los colores P1–P4 son por puesto, no por jugador.

### 3.4 Partida (`session`)

| Comando | Payload |
|---|---|
| `kantor.session.get` | `{ sessionId? }` |
| `kantor.session.list` | `{ offset, limit ≤ 50 }` |
| `kantor.session.start` | `{ name? (≤ 48), onOpen: "fail"\|"closeAndStart" }` |
| `kantor.session.close` | `{ sessionId }` |
| `kantor.session.addPlayer` / `removePlayer` | `{ sessionId, playerId }` |
| `kantor.session.history` | `{ sessionId, offset, limit ≤ 50, lang? }` → `[PerformanceSummary]` |

- Una sola abierta. Cerrar Kantor no la cierra. `start` con otra abierta: `SESSION_OPEN` o la cierra
  (`closeAndStart`, en una transacción), sin heredar jugadores.
- Archivar un jugador lo quita de la selección de la partida abierta; no borra historial.

### 3.5 Reproducción (`playback`)

| Comando | Payload | Estados válidos |
|---|---|---|
| `kantor.playback.start` | `{ libraryEntryId \| itemId, slots?, sessionPolicy? }` | `idle` o `finished`, y con la pantalla en menú, biblioteca, cola, escenario o resultados (ver 11) |
| `kantor.playback.confirmSingers` | `{}` | `screen = who-sings` |
| `kantor.playback.pause` | `{}` | `playing` (ya `paused` ⇒ ok, sin cambio) |
| `kantor.playback.resume` | `{}` | `paused` (ya `playing` ⇒ ok) |
| `kantor.playback.restart` | `{}` | `playing`, `paused`, `finished` |
| `kantor.playback.stop` | `{}` | `playing`, `paused`, `preparing` |
| `kantor.playback.next` | `{}` | `idle`, `finished` |
| `kantor.navigate.menu` | `{}` | cualquiera; detiene la canción |
| `kantor.results.skipAnimation` / `close` | `{}` | `finished` |
| `kantor.settings.setAutoAdvance` | `{ enabled }` | cualquiera |
| `kantor.autoAdvance.cancel` | `{}` | cualquiera (sin cuenta activa ⇒ ok, sin cambio) |

- **`sessionPolicy`**: `current` | `new` | `solo`. Omitido: `current` si hay partida abierta; **sin partida es
  obligatorio** (si no, `SESSION_REQUIRED`). `current` sin partida ⇒ `SESSION_REQUIRED`; `new` con una abierta ⇒
  `SESSION_OPEN` (usar antes `session.start closeAndStart`); `solo` no guarda resultado y avisa `SOLO_NOT_SAVED`.
- `start` responde cuando Kantor **aceptó**; el resto se ve por estado. Faltando decisiones locales queda en
  `preparing` con `screen: who-sings` (o `who-plays`, `session-gate`). Entrada `available:false` ⇒
  `ENTRY_UNAVAILABLE`.
- **`confirmSingers`**: exige ≥ 1 puesto asignado, cada `playerId` activo y de la partida, cada `slot` ≤ `maxSlots`
  y con micrófono. Si la PC lo confirma antes, el segundo recibe `INVALID_STATE`.
- **`restart`**: equivale al "Otra vez" local: detiene lo actual **sin guardarlo**, conserva puestos y jugadores,
  **no** vuelve a preguntar quién canta y arranca de cero con un `performanceId` nuevo. Desde `finished`, el
  resultado anterior ya está guardado y no se toca, y la nueva interpretación no cuelga de ningún elemento de cola
  (`queueItemId: null`). Desde `playing/paused`, el elemento `running` sigue `running`. En `idle/preparing` ⇒
  `INVALID_STATE`.
- `stop` y `navigate.menu` cancelan: **no se guarda nada**, ni parcial; el elemento `running` pasa a `cancelled`.
- No hay comandos para dificultad, retardo, canal, dispositivo ni motor; `start`, `restart` y `next` durante una
  carga o interpretación devuelven `BUSY`/`INVALID_STATE` (las mismas reglas que la interfaz local).

### 3.5.1 Avance automático

- Ajuste `UserSettings.AutoAdvance`, **apagado por omisión**; se cambia en Opciones › Juego y con
  `settings.setAutoAdvance`. Se publica en `settings.autoAdvance`.
- **Se arma** cuando la pantalla de resultados termina de animar; si la animación se omite (Enter/Espacio local o
  `results.skipAnimation`), **en ese momento**. La pulsación que omite la animación solo omite: no cancela.
  La cuenta dura **10 s**, se muestra en la pantalla de resultados de Kantor y se publica en
  `autoAdvanceCountdown`.
- **Cancelan** la cuenta (solo si son *aceptados*; uno rechazado no cambia nada): cualquier tecla o clic local en
  resultados posterior a la animación (el movimiento del mouse no), `results.close`, `navigate.menu`, cualquier
  `playback.*`, `autoAdvance.cancel` y apagar `autoAdvance` (`setAutoAdvance {false}`).
- **No** la cancelan: `queue.*`, `library.*`, `players.*`, `selection.set`, consultas ni otros ajustes.
- Cancelada, **no se vuelve a armar** para esos resultados; activar el ajuste con los resultados ya en pantalla
  tampoco los arma.
- **Al llegar a 0** Kantor inicia el primer pendiente como el flujo local ("SIGUIENTE") saltando las pantallas de
  decisión: partida actual, con los `slots` del elemento o, si no tiene, la selección vigente de la partida.
- **Nunca avanza sin** un pendiente `available`, una partida abierta (no `solo`) y cantantes válidos (jugadores
  activos y de la partida, `slot ≤ maxSlots`, micrófono creado). Se valida **al armar y otra vez al disparar**; si
  falla en cualquiera de las dos, no inicia, cancela la cuenta y publica `attention AUTO_ADVANCE_BLOCKED {reason}`.
  Como `queue.*` no cancela, el `itemId` de la cuenta es "el primero pendiente al disparar" y se republica si cambia.

### 3.6 Resultados e historial (`results`)

| Comando | Payload |
|---|---|
| `kantor.results.last` | `{ lang? }` |
| `kantor.history.performance` | `{ performanceId, lang? }` |
| `kantor.history.player` | `{ playerId, offset, limit ≤ 50, lang? }` |

`PerformanceDetail = { performanceId, sessionId, songIdentity, libraryEntryId?, artist, title, edition?,
finishedAt, difficulty, scoringVersion, rankingVersion, results: [{ playerId, name, slot, score, position,
competitionPoints, seal: {rankId, text}, commentId?, comment? }] }`

- **Limitación del historial**: el historial **existente** guarda `songIdentity` (artista+título) y una
  instantánea de artista y título, **no la edición concreta**. En esas interpretaciones `libraryEntryId` y `edition`
  son `null`. Las interpretaciones **nuevas** guardan el `libraryEntryId` (columna nueva, opcional) y la edición
  como instantánea. Las estadísticas siguen agrupando por `songIdentity`. Nunca se infiere la edición de lo viejo.
- `lang` (`es`|`en`): idioma de `seal.text` y `comment`; por omisión, el configurado en Kantor. Los IDs no dependen
  del idioma.
- `position` con empates compartidos (1, 1, 3); `competitionPoints` y `seal` son **derivados** de las reglas
  vigentes (`rankingVersion`, `ResultRank`); `name` es el nombre actual del jugador.
- **Consultar nunca sortea comentarios**: `commentId` se persiste en `performance_result`, en la misma transacción
  que el resultado. Interpretaciones previas a la migración: `commentId`/`comment` = `null`.
- `kantor.result` (evento) al terminar una canción: el mismo `PerformanceDetail` + `saved`. Cantada suelto llega
  con `saved:false`. Una interpretación interrumpida o cancelada no genera resultado ni evento.

### 3.7 Pantalla

Solo `kantor.screen.setFullscreen { enabled }`. Mover entre monitores y traer al frente son `windows.move` y
`windows.focus` de Avyn; `state.display` les da con qué apuntar (2.1).

### 3.8 Sistema

`kantor.state.get` (snapshot completo, sin `commandId`) · `kantor.ping`.

### 3.9 Mutaciones que exigen `commandId` + `origin` (lista exacta)

`queue.add` · `queue.remove` · `queue.move` · `queue.setNext` · `queue.setSlots` · `queue.clear` ·
`players.create` · `players.rename` · `players.archive` · `players.setCharacter` · `selection.set` ·
`session.start` · `session.close` · `session.addPlayer` · `session.removePlayer` ·
`playback.start` · `playback.confirmSingers` · `playback.pause` · `playback.resume` · `playback.restart` ·
`playback.stop` · `playback.next` · `navigate.menu` · `results.skipAnimation` · `results.close` ·
`settings.setAutoAdvance` · `autoAdvance.cancel` · `screen.setFullscreen`.

**Lecturas** (sin `commandId`): `state.get` · `ping` · `library.search/get/facets/cover` · `queue.list` ·
`players.list` · `characters.list` · `session.get/list/history` · `results.last` · `history.performance/player`.

## 4. Eventos

`kantor.hello` · `kantor.state` · `kantor.state.changed` (avanza `rev`) · `kantor.result` · `kantor.score.tick`.
Los cambios, también los hechos desde la PC, salen siempre por `state.changed`; cola, partida, atención y cuenta
regresiva son secciones, no eventos aparte. Nunca por temporizador, salvo `score.tick`.

## 5. Errores

`{ code, message, details? }`; `message` es técnico y en inglés (Avyn traduce por `code`).

| Código | Cuándo |
|---|---|
| `INVALID_PAYLOAD` | campo faltante, tipo o rango inválido, `commandId`/`origin` ausente |
| `NOT_FOUND` | id inexistente (`details.reason`: `unknown`\|`gone`; `details.field` indica cuál) |
| `INVALID_STATE` | el estado actual no permite el comando |
| `BUSY` | cargando o interpretando |
| `CONFLICT` | `expectedQueueRev`/`expectedSelectionRev` vencido (`details` trae lo vigente) |
| `COMMAND_ID_REUSED` | mismo `commandId`, otro comando o payload |
| `ITEM_RUNNING` | mover o quitar el elemento en ejecución |
| `QUEUE_FULL` | 100 pendientes |
| `SESSION_OPEN` / `SESSION_REQUIRED` | ver 3.4 y 3.5 |
| `PLAYER_ARCHIVED` / `PLAYER_NOT_IN_SESSION` / `PLAYER_NAME_TAKEN` | reglas de jugadores |
| `SLOT_UNAVAILABLE` / `NO_MICROPHONES` | puesto fuera de `maxSlots` o sin micrófonos creados |
| `ENTRY_UNAVAILABLE` | la entrada de biblioteca no está disponible (`libraryEntryId` en `details`) |
| `NEEDS_LOCAL_ACTION` | requiere una persona en la PC (`details.screen`) |
| `UNKNOWN_COMMAND` / `CAPABILITY_UNAVAILABLE` / `PROTOCOL_ERROR` | ver 1.6 |
| `TIMEOUT` | venció la espera en cola de Kantor; no se ejecutó |
| `INTERNAL_ERROR` | excepción; `applied: "unknown"` |

### 5.2 Códigos de atención

`attention[] = { code, params? }`; Avyn traduce por `code` (no se envía `message`); un código desconocido se muestra
genérico. Iniciales: `AUDIO_ENGINE_FALLBACK {requested,active}` · `AUDIO_DEVICE_LOST` · `NO_MICROPHONES` ·
`DATABASE_UNAVAILABLE` · `SAVE_FAILED` · `SOLO_NOT_SAVED` · `NO_LIBRARY_FOLDERS` · `LIBRARY_SCAN_FAILED` ·
`SONG_LOAD_FAILED {libraryEntryId}` · `VIDEO_UNAVAILABLE` · `AUTO_ADVANCE_BLOCKED {reason}`.
Avisan lo que requiere una persona frente a la PC; no hay comandos para resolverlos.

## 6. Límites

Marco 1 048 576 B · página de búsqueda 50 ítems o 256 KiB · cola 100 pendientes · `queue.list` 100 · historial 50 ·
consulta 100 car. · nombre de jugador 1–24 (único entre activos, sin distinguir mayúsculas) · nombre de partida ≤ 48 ·
`origin.name` ≤ 48 · `libraryEntryId` ≤ 64 · puestos 1..`maxSlots` ≤ 4 · `commandId` 8–64 · caché de
`commandId` 1024 / 15 min · portada ≤ 96 KB · ping 5 s, muerte a los 15 s + 5 s de gracia · cuenta regresiva 10 s.
Todo argumento se valida **antes** de tocar el estado: un comando inválido no cambia nada ni emite eventos.

## 7. Hilos

- **Dispatcher WPF**: todas las mutaciones y toda lectura del estado de pantalla. Los comandos se encolan y corren
  de a uno; cada mutación publica como máximo una `state.changed`.
- **Hilo de E/S del pipe**: lectura de marcos, `ping`, registro de actividad y escritura. No espera al dispatcher.
- **Fuera del dispatcher**: `library.*` (sobre el snapshot inmutable), decodificación de portadas y lecturas de
  historial. `KantorStore` usa **una** conexión con un candado global (seguro entre hilos); las lecturas pesadas se
  paginan para no demorar el guardado de una interpretación.

## 8. Qué incluye la implementación en Kantor

- Esquema SQLite **v3**: `library_entry`; `queue_item` con `item_id`, `library_entry_id`, instantánea
  (`song_identity`, artista, título, edición), `state`, `added_by`, `added_at`, `finished_at`, `performance_id` y
  jugadores por elemento; `performance.library_entry_id` y `performance.edition` (nulos en lo existente);
  `performance_result.comment_id`. Migración automática (la cola v2 se convierte creando entradas por ruta); una
  base más nueva se rechaza, como hoy.
- `KantorController` en Core (sin WPF): una sola ruta de operaciones para interfaz local e IPC.
- `KantorIpcServer` (App); `NativeMonitor` con `deviceName` y píxeles físicos; manifiesto PerMonitorV2;
  republicación de `display`; miniaturas; ajustes `RemoteControl` y `AutoAdvance`; cuenta regresiva en resultados.
- Pruebas automáticas: cliente de pipe real contra el controlador real; dos clientes; reconexión; reinicio; límite
  de marco; ping, suspensión y expulsión; migración; ids de entrada (mover, borrar, duplicar).

## 9. Cambios desde la v2 (pedidos de Avyn y cierre)

Se aplicaron los 23 ajustes de la ronda anterior (límite de marco, `commandId` con huella, `origin`,
`INVALID_PAYLOAD`/`INTERNAL_ERROR`, `rev` unificado, snapshot e `instanceId`, páginas por tamaño, portadas bajo
demanda, timeouts, sin reintento tras reinicio, `queue.list{sessionId}`, `facets` y estado de escaneo, `lang`,
códigos de atención, posición sin hora absoluta, `display`, lecturas fuera del dispatcher, lista exacta de
`commandId`, `sessionPolicy`, compatibilidad aditiva, `selectionRev`, estados de `restart`) y, en el cierre:

- `songId` eliminado: `libraryEntryId` + `songIdentity`; ediciones sin colapsar; `Song.edition` vuelve; búsqueda por
  `songIdentity`; portadas por entrada; `SONG_UNAVAILABLE` pasa a `ENTRY_UNAVAILABLE`.
- Avance automático: `autoAdvanceCountdown`, `autoAdvance.cancel`, reglas de cancelación y armado.
- Presencia: garantías de 1.1.
- `display`: PerMonitorV2, republicación, título no vacío, fullscreen y movimiento documentados.
- `commandId` transformado por el Agent por dispositivo; `origin` del Agent.

## 11. Precisiones de la implementación (aditivas; no cambian lo acordado)

- **`Song.voices` no se publica**: el índice de la biblioteca no cuenta voces. Se agregaría como campo opcional
  cuando haga falta (requiere leer el TXT completo).
- **`slots.availableSlots`** (campo nuevo): los puestos que tienen micrófono. `maxSlots` es la cantidad; como un
  micrófono quitado puede dejar huecos (M1 y M3), la validación de `slot` usa `availableSlots`. Un puesto fuera de
  la lista da `SLOT_UNAVAILABLE`; sin ningún micrófono, `NO_MICROPHONES`.
- **`queue.list`** agrega `sessionId` y `readOnly` (la cola de una partida que no es la abierta). **`selection.set`**
  responde `{ selectionRev }`. **`queue.*`** responde `{ queueRev, item? , removed? }`. **`players.*`** responde el
  jugador. **`session.start`** responde la partida (`sessionId`, `isOpen`, `playerIds`, `performanceCount`).
- **`playback.start`** (valida el estado en este orden): `BUSY` si suena, está pausada o carga;
  `NEEDS_LOCAL_ACTION` con `details.screen` en Opciones, Jugadores, puerta de partida, ¿quién juega? y ¿quién canta?;
  desde menú, biblioteca, cola, escenario sin empezar y resultados sí se acepta. Responde `{ accepted: true }`.
- **`playback.next`** con la cola vacía: `INVALID_STATE` con `details.reason = "queue-empty"`; con un pendiente no
  disponible: `ENTRY_UNAVAILABLE`.
- **`BUSY` mientras se canta** también para `session.start` (con `closeAndStart`), `session.close`,
  `players.archive`, `session.removePlayer` de alguien que canta y `selection.set`: cambiarían una interpretación en
  curso. Con `INVALID_STATE` (`details.reason = "session-closed"`) se rechaza modificar una partida cerrada.
- **`playback.stop`** en `preparing` cancela la carga o la decisión local y deja a Kantor en el menú; en
  `playing`/`paused` deja la canción cargada (como "Detener"). En ambos casos el elemento en ejecución pasa a
  `cancelled` y no se guarda nada.
- **Códigos de atención emitidos hoy**: `AUDIO_ENGINE_FALLBACK`, `DATABASE_UNAVAILABLE`, `NO_LIBRARY_FOLDERS`,
  `NO_MICROPHONES`, `SOLO_NOT_SAVED`, `SAVE_FAILED`, `VIDEO_UNAVAILABLE`, `SONG_LOAD_FAILED {libraryEntryId}`,
  `AUTO_ADVANCE_BLOCKED {reason}` con `reason` ∈ `queue-empty`, `entry-unavailable`, `no-session`, `no-singers`,
  `invalid-singer`, `no-microphone`. `AUDIO_DEVICE_LOST` y `LIBRARY_SCAN_FAILED` quedan reservados (no hay hoy
  un punto que los detecte).
- **Pipe**: `PipeOptions.CurrentUserOnly` (ACL al usuario actual). Si otra instancia de Kantor ya tiene el pipe, el
  servidor reintenta cada 3 s y lo deja en el log (Opciones › Juego muestra "No se pudo abrir el canal local").
- **Monitores**: `display.monitor.deviceName` sale de `MONITORINFOEX.szDevice` y `bounds` de `GetMonitorInfo` con la
  aplicación en PerMonitorV2 (píxeles físicos).

## 10. Verificaciones que requieren hardware o manos (no son bloqueos del contrato)

Dos monitores con resoluciones y escalas distintas (reajuste de pantalla completa y maximizada al mover); aspecto de
la interfaz pixelada bajo PerMonitorV2 con escalas no enteras; dos o más micrófonos simultáneos; suspensión e
hibernación con el Agent conectado; cuenta regresiva real al terminar una canción cantada.
