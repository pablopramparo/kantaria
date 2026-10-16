# Personajes

Cada personaje vive en `Assets/Characters/<id>/` con dos PNG con transparencia real:

- `portrait.png`: retrato de cabeza y hombros (selección de jugadores y durante la canción). No es el cuerpo
  entero reducido.
- `fullbody.png`: cuerpo entero, para la ceremonia de resultados (recorte ajustado, sin márgenes innecesarios).

El `<id>` usa minúsculas, dígitos, `-` y `_`, y no cambia entre versiones. Los personajes se declaran en
`Kantor.Core/Characters/CharacterCatalog.cs` (nombre y `CharacterTuning` opcional). Si falta un PNG, la
interfaz usa su placeholder. No hay dimensiones obligatorias.
