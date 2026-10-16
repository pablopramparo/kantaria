# Personajes

Cada personaje vive en `Assets/Characters/<id>/` con dos PNG con transparencia real:

- `portrait.png`: retrato de cabeza y hombros (selección de jugadores y durante la canción). No es el cuerpo
  entero reducido.
- `fullbody.png`: cuerpo entero, para la ceremonia de resultados (recorte ajustado, sin márgenes innecesarios).

El `<id>` usa minúsculas, dígitos, `-` y `_`, y no cambia entre versiones. Los personajes se declaran en
`Kantor.Core/Characters/CharacterCatalog.cs` (nombre y `CharacterTuning` opcional). Si falta un PNG, la
interfaz usa su placeholder. No hay dimensiones obligatorias.

## Ediciones: local y Microsoft Store

La edición **local** (instalador y zip) trae todos los personajes. La de la **Store** es un subconjunto: los ids listados
en `Kantor.Core/Characters/store-excluded.txt` (separados por `;` o por líneas) no se ofrecen ni se embeben en esa
edición (`scripts/publish.ps1 -Edition Store`; ver `CharacterEdition`). Un jugador creado en la edición local con un
personaje que la Store no trae se ve con el genérico (el primero disponible); no se pierde ningún dato.
Un test valida que la lista solo nombre avatares reales.
