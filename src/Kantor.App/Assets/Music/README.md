# Música de la interfaz

Dos temas en loop para acompañar la navegación y la pantalla de resultados. Ambos están publicados en OpenGameArt bajo
**CC0 1.0** (dominio público; <https://creativecommons.org/publicdomain/zero/1.0/>): no exigen atribución, pero se conserva.
La licencia de cada página se verificó el 16/10/2026. No están bajo la GPL de Kantaria ni bajo `LICENSE-ASSETS.md`.

| Archivo distribuido | Tema | Autor | Origen | Licencia |
|---|---|---|---|---|
| `8bit-title-screen.mp3` | **8Bit Title Screen** (inicio y navegación) | Joth (créditos del archivo: JPool; "(Game-1) Theme") | <https://opengameart.org/content/8bit-title-screen> | CC0 |
| `heroic-loop.wav` | **Heroic Loop** (resultados) | Zane Little Music | <https://opengameart.org/content/heroic-loop> | CC0 |

## Archivos originales y conversiones

- **8Bit Title Screen**: se usa el MP3 original sin modificar (`8Bit Title Screen.mp3`, 356 018 bytes, 44,1 kHz estéreo, 128 kbps,
  SHA-256 `0fd7f99ae0462789…`). El loop se recorta **al reproducir** (no en el archivo) a los 967 680 cuadros válidos que declara
  su etiqueta de iTunes (`iTunSMPB`): el decodificador de Windows entrega unos 1 776 cuadros de relleno al final que se descartan,
  y con eso la costura del loop es continua (comprobado midiendo el salto entre el último y el primer cuadro).
- **Heroic Loop**: el original (`heroic_loop_0.wav`, 25 644 744 bytes, PCM de **32 bits en coma flotante**, 48 kHz estéreo, 66,78 s,
  SHA-256 `f884d34efc44a8d8…`) se convirtió a **PCM de 16 bits** (mismo muestreo y duración, 12 822 306 bytes) con NAudio
  (`WaveFileWriter.CreateWaveFile16`), sin otro procesamiento, para reducir el tamaño a la mitad. Es el único cambio. La costura
  del loop ya era continua.

## Cómo suenan en Kantaria

Los archivos se copian junto al ejecutable (`Assets\Music\`, contenido de la compilación y de la publicación) y se leen desde
`AppContext.BaseDirectory`: no hay rutas absolutas ni descargas en tiempo de ejecución. Si falta alguno, esa pista simplemente no
suena. La lógica está en `Kantor.Core/Music/MusicCoordinator.cs`; el volumen se ajusta en Opciones › Audio › Música de interfaz.
