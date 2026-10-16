# Acceso de invitados por LAN

Los invitados usan el celular para **buscar una canción, elegir quién la canta y agregarla a la cola**. Nada más.
Kantor sigue funcionando igual sin esto: el servidor está **apagado por omisión** y se apaga solo al cerrar Kantor.

## Cómo se usa

1. En el **menú principal** o en la **lista de canciones** (abajo a la derecha) tocá **INVITADOS · QR**. Se abre el
   acceso y aparece el QR. Tocá el QR para verlo grande (pantalla "SUMATE AL KARAOKE" / "JOIN THE PARTY"), donde
   también se **apaga** (APAGAR).
2. El celular tiene que estar en **la misma red Wi-Fi** que la PC. Escanear el QR con la cámara abre la página.
3. El invitado busca, toca una canción, elige cantantes (hasta tantos como micrófonos creados en
   Opciones › Audio) y toca **AGREGAR A LA COLA**. Aparece "¡EN COLA!" con su puesto.
4. Para cerrar: **APAGAR** en la pantalla del QR (o cerrar Kantor). Se revoca el token y se cierra el puerto.

Si la PC tiene varias redes, Kantor elige la que tiene puerta de enlace; se puede fijar otra en
**Opciones › Juego › Red para invitados**. Si cambia con el acceso activo, se reabre en la nueva (con otro QR).

## Qué pueden hacer los invitados

| Permitido | No permitido |
|---|---|
| Buscar canciones (título, artista…, sin importar mayúsculas ni acentos) | Crear, editar o borrar jugadores; perfiles temporales |
| Elegir cantantes entre los jugadores **ya existentes** (los de la partida abierta; sin partida, los activos) | Micrófonos, avatares, ajustes |
| Agregar la canción a la cola | Reproducir, pausar, saltar, detener; reordenar o quitar de la cola |
| | Historial, puntajes, estadísticas, Avyn, archivos de la PC |

El primer cantante elegido ocupa el primer micrófono creado, el segundo el siguiente, etc. En un dueto, cada
micrófono canta la voz que le corresponde por la regla de siempre (se puede cambiar en "¿Quién canta?" en la PC).
Sin jugadores o sin micrófonos la página lo avisa: se crean desde la PC. Al cantar un elemento de la cola desde la
PC (CANTAR YA / SIGUIENTE), los cantantes elegidos por el invitado quedan cargados en "¿Quién canta?" si siguen
siendo válidos.

## Cómo está hecho

- Servidor **Kestrel** en `Kantor.Core/Guest/`, escuchando **solo en una dirección IPv4 privada** (10/8, 172.16/12,
  192.168/16) de una interfaz activa y no virtual; nunca en `0.0.0.0` ni en el loopback. Puerto 8765 (o el
  siguiente libre).
- Todo pasa por lo que ya existe: `LibraryCatalog`/`LibrarySearch` (misma búsqueda que la PC y Avyn),
  `KantorStore` (jugadores), `QueueService` (cola) y `KantorController.GuestEnqueue`, que reutiliza la validación
  de `queue.add` y corre en el hilo de la interfaz, serializado con Avyn y la PC. No se toca el scoring, la
  reproducción, el audio ni el contrato de Avyn (el pipe no se expone).
- La página (`Guest/Web/`: HTML, CSS, JS sin frameworks) sale de **recursos embebidos por nombre fijo**, con
  Jersey 15, el logo y los valores de color y marco de `Theme.xaml`, `PixelFrame` y `SongListView.xaml` en
  variables CSS. Sin CDN, sin servicios externos.
- Rutas: `GET /` y `/assets/*` (lista cerrada) y, con token, `GET /api/config|songs|players`, `GET /api/cover/{id}`,
  `GET /api/avatar/{id}` y `POST /api/queue`. Portadas y avatares se piden por identificador, nunca por ruta.

## Seguridad (y sus límites)

- **Token** de 128 bits aleatorios (criptográfico), nuevo cada vez que se activa y revocado al apagar. Viaja en el
  fragmento del QR (`#k=…`: el navegador no lo manda al servidor ni en `Referer`) y luego en el encabezado
  `X-Kantor-Token`. No se acepta en la URL, no se escribe en registros y solo sirve para esta función.
  Es una **credencial compartida**: quien tenga el enlace puede usar el acceso mientras esté activo.
- Rechaza clientes que no sean de una red privada; valida `Host` (anti-rebinding) y `Origin`; sin CORS; cabeceras
  CSP/`nosniff`/`no-referrer`; cuerpo máximo 1 KiB; JSON estricto; límite de pedidos por cliente (120 / 10 s),
  de agregados (6 / 30 s), bloqueo temporal tras 10 tokens inválidos por minuto y máximo de 32 pedidos a la vez.
- Doble toque y reintentos: el mismo `requestId` (la página lo reusa hasta que se cambia la elección o se vuelve
  a la lista) agrega **una sola vez**. Un pedido nuevo siempre agrega, aunque sea la misma canción con los mismos
  cantantes: repetir una canción es legítimo.
- **HTTP no está cifrado**: quien pueda observar el Wi-Fi puede ver el token y el tráfico. No lo uses en redes
  que no controles. Que la IP sea privada no basta para aislar de internet: un reenvío de puertos del router la
  expondría, por eso Kantor no abre puertos (sin UPnP) y rechaza clientes públicos. No lo publiques en el router.
- El **Firewall de Windows** pregunta la primera vez: permitilo solo en **redes privadas**.
- **Wi-Fi con aislamiento de clientes** (redes de invitados, algunos routers, redes de hoteles u oficinas) impide
  que el celular llegue a la PC: usá una red sin aislamiento o el hotspot de otro celular.

## Comprobado y pendiente

Ver `docs/ESTADO.md` §8 duodecies.
