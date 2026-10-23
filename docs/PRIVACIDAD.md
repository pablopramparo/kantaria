# Política de privacidad de Kantaria

Última actualización: 16 de octubre de 2026. (English version: [PRIVACY.md](PRIVACY.md).)

Kantaria es un karaoke local para Windows. Esta política explica qué datos usa la aplicación y qué pasa con ellos.
Resumen: **Kantaria no envía ningún dato a nadie. Todo queda en tu PC.**

## Quién es el responsable

Pablo Pramparo, autor de Kantaria (<https://github.com/pablopramparo/kantaria>). Para consultas, abrí un *issue* en ese
repositorio.

## Qué datos usa Kantaria y dónde quedan

Todo se guarda en tu equipo, en la carpeta `%APPDATA%\Kantor` (en la versión de Microsoft Store, dentro del espacio
privado que Windows le da al paquete):

- **Micrófono.** Kantaria lee el micrófono mientras cantás para detectar la altura de la voz y calcular el puntaje. **No
  graba ni guarda el audio, ni lo reproduce, ni lo transmite.** Solo se conserva el resultado del análisis en memoria
  mientras suena la canción.
- **Jugadores y partidas.** Los nombres que escribís, el personaje elegido, las partidas, los puntajes y el historial
  de canciones cantadas se guardan en una base de datos local (`kantor.db`). Podés borrar partidas y exportar o
  respaldar los datos desde la aplicación.
- **Ajustes** (`settings.json`): idioma, dispositivos de audio, carpetas de canciones y preferencias.
- **Biblioteca de canciones** (`library-index.json`): un índice de los archivos de las carpetas que vos elegís. Kantaria
  no incluye canciones ni las copia ni las sube a ningún lado.
- **Registro de diagnóstico** (`kantor.log`): marcas de tiempo y mensajes técnicos para detectar problemas de la
  propia aplicación. No contiene audio ni contenido de tus canciones.
- **Personajes propios**: si cargás imágenes, se copian a la carpeta de datos.

## Conexión a internet

Kantaria **no se conecta a internet**: no tiene cuentas, publicidad, estadísticas de uso, informes de errores ni
actualizaciones automáticas, y no usa servicios de terceros. Las únicas conexiones son las dos opcionales de abajo, que
son locales.

## Funciones opcionales (apagadas por omisión)

- **Acceso de invitados por red local.** Si lo activás, Kantaria abre un servidor HTTP en tu red local para que los
  celulares busquen canciones, creen un jugador (nombre y personaje) y las agreguen a la cola. Solo escucha en la
  dirección privada de tu red, usa una clave temporal que va en el QR y se revoca al apagarlo o al cerrar Kantaria, y no
  se expone a internet. **El tráfico es HTTP sin cifrar**: quien pueda observar tu red Wi-Fi podría verlo, así que
  activalo solo en redes que controlás. Los nombres que escriben los invitados se guardan como jugadores en tu base
  de datos local. Kantaria no guarda las direcciones de los celulares.
- **Control remoto (Avyn).** Un canal local (*named pipe*) solo accesible por tu usuario de Windows, para controlar
  Kantaria con otra aplicación instalada en la misma PC. No usa la red.

## Con quién se comparten los datos

Con nadie. Como Kantaria no envía datos, no hay terceros que los reciban. Los datos son tuyos: están en tu equipo y los
controlás vos.

## Cómo borrar tus datos

Desinstalar Kantaria y borrar la carpeta `%APPDATA%\Kantor` elimina todo. Dentro de la aplicación podés borrar partidas
y archivar jugadores. En la versión de la Store, desinstalar el paquete borra también su espacio de datos.

## Menores de edad

Kantaria no recopila datos personales de nadie, incluidos los menores. Si lo usan niños, los nombres que escriban quedan
solo en el equipo donde se use.

## Cambios en esta política

Si algo cambia (por ejemplo, si una versión futura agregara alguna conexión), se actualizará este documento y la fecha
de arriba, y la aplicación no empezará a enviar datos sin avisarlo claramente.
