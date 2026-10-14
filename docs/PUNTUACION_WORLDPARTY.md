# Puntuación de UltraStar WorldParty: referencia de comportamiento

Descripción en palabras propias de lo que hace WorldParty (GPL-3.0+), leída en `src/base/UNote.pas`,
`src/base/UMusic.pas`, `src/base/USong.pas`, `src/base/UBeatTimer.pas`, `src/base/URecord.pas`,
`src/base/UIni.pas` y `src/screens/controllers/UScreenSingController.pas` (rama `master`, leída el
2026-10-14). **No se copió código**; esto es la especificación de comportamiento contra la que se
mide Kantor. Lo que no figura acá no se verificó.

## 1. Tipos de nota y peso

| Tipo (TXT) | Marca | Factor de puntaje | ¿Compara tono? | Bolsa |
|---|---|---|---|---|
| Normal | `:` | 1 | sí | Normal |
| Dorada | `*` | 2 | sí | Dorada |
| Rap | `R` | 1 | **no** (basta con sonido sobre el umbral) | Normal |
| Rap dorada | `G` | 2 | **no** | Dorada |
| Freestyle | `F` | 0 | no puntúa | — |

Las notas de largo 0 se ignoran.

## 2. Valor de la canción y puntos por beat

- `ScoreValue` de una voz = suma de `largo × factor` de todas sus notas (freestyle aporta 0).
- Máximo de la canción: `MAX_SONG_SCORE = 10000`. Con bono de línea activado, las notas valen como
  máximo `10000 − 1000 = 9000`; el bono de línea aporta hasta 1000. Con el bono apagado, las notas
  valen hasta 10000.
- Cada **beat acertado** suma `(MaxNotas / ScoreValue) × factor`. Un beat de nota dorada vale el doble
  que uno normal. No importa en qué parte de la nota se cante: cada beat se evalúa por separado.
- Las notas normales y rap suman a `Score`; las doradas y rap doradas a `ScoreGolden`.

## 3. Evaluación por beat

- Se evalúa **una vez por cada beat** de la canción, en orden. Para cada uno se busca la nota (no
  freestyle, largo > 0) que lo cubre: `Start ≤ beat ≤ Start + Length − 1`.
- Si hay nota y el micrófono tiene un tono "válido", se compara; si no, el beat no suma.
- El tono "válido" se decide **solo por volumen**: el máximo absoluto de las primeras 1024 muestras de
  un búfer de 4096 debe ser ≥ umbral (`0,05 / 0,10 / 0,15 / 0,20`; por omisión `0,10`, es decir 10 %
  de la escala completa). No hay criterio de confianza del detector: si pasa el volumen, siempre hay
  un tono.
- El tono detectado es el semitono más cercano sobre una grilla que arranca en C2 (65,4064 Hz), y para
  comparar se usa **solo la clase de altura** (`semitono mod 12`).

## 4. Octavas y tolerancia

1. Se lleva el tono detectado por octavas completas hasta que `tono_cantado − tono_nota` quede en
   `[−6, +6]`. Equivale a la distancia circular mínima (0 a 6 semitonos).
2. Es acierto si `|tono_nota − tono_cantado| ≤ tolerancia`, o si la nota es rap / rap dorada.
3. **Tolerancia = `2 − nivel`**:

   | Nivel | Tolerancia |
   |---|---|
   | Fácil (0) | 2 semitonos |
   | Medio (1) | 1 semitono |
   | Difícil (2) | 0 (tono exacto) |

   En juego normal el nivel es **por jugador** (`PlayerLevel`); en otros modos es la dificultad global.
   Por omisión: Fácil.
4. Un tono que cae fuera de la tolerancia no da puntos parciales: el beat se pierde.

## 5. Sincronización de la detección

La detección usa su propio beat, atrasado respecto del que se muestra: `beatD = −0,5 + beat(t − (GAP + 90 ms))`.
Es decir, **90 ms fijos y medio beat** de compensación de latencia, sin medición. Además, el tono que se
compara en cada beat es **el más reciente del búfer** en el momento en que se procesa el cuadro; si en
un cuadro se descubren varios beats, todos usan el mismo tono. El resultado depende un poco de la tasa
de cuadros.

## 6. Bono de línea (solo con bono activado; activado por omisión)

Se calcula al terminar cada frase (cuando el beat de detección pasa del fin de su última nota):

- `MaxLineScore = MaxNotas × (valorDeLaFrase / ScoreValue)`, con `valorDeLaFrase` = suma de `largo × factor`.
- `LineScore` = puntos (normal + dorada, sin redondear) ganados desde el fin de la frase anterior.
- `Perfección = LineScore / (MaxLineScore − 2)`, limitada a `[0, 1]`. Si `MaxLineScore ≤ 2` vale 1.
  Los 2 puntos de holgura hacen que no haga falta ser perfecto.
- `BonoPorLínea = 1000 / (cantidadDeFrases − frasesVacías)`; es igual para todas, sin importar el largo.
- `ScoreLine += BonoPorLínea × Perfección`.
- **Frase vacía** = valor 0 (sin notas o solo freestyle): no cuenta para el reparto ni da bono.

## 7. Redondeo y total

Todo se acumula en reales y se muestra en múltiplos de 10:

- `ScoreInt = round(Score / 10) × 10` (el `round` de Pascal redondea .5 al par).
- `ScoreGoldenInt`: si `ScoreInt < Score`, `ceil(ScoreGolden / 10) × 10`; si no, `floor(ScoreGolden / 10) × 10`.
  Se redondean en sentidos opuestos para que el máximo sea 10000 y no 10010.
- `ScoreLineInt = floor(ScoreLine / 10) × 10`.
- `Total = ScoreInt + ScoreGoldenInt + ScoreLineInt`.

## 8. Dúos y otras particularidades

- En un dueto cada voz tiene su propio `ScoreValue`, y la voz de cada jugador se asigna por paridad del
  índice de jugador. Quedó fuera del alcance del primer incremento.
- El conteo de líneas del bono es por voz.

## 9. Diferencias deliberadas de Kantor (implementado en `Kantor.Core.Scoring`)

Lo que **se mantiene igual**: máximo 10.000 (9000 + 1000 de bono), factores 1/2/1/2/0, tolerancia 2/1/0
semitonos, equivalencia por clase de altura, rap sin comparar tono, valor por beat, perfección con 2 puntos
de holgura, reparto del bono entre frases no vacías, redondeo a decenas con el sentido opuesto en las doradas,
dificultad Fácil y bono activado por omisión.

| Tema | WorldParty | Kantor | Por qué |
|---|---|---|---|
| Detector | Autocorrelación por semitonos, sin confianza | YIN: voz/no voz | Decisión del proyecto (mantener YIN) |
| ¿Hay sonido? | Pico ≥ umbral de amplitud (10 % por omisión) | `LevelDb` RMS ≥ `LevelGateDb` (−50, provisional, configurable); rap y notas con tono | Sin equivalencia afirmada; se calibra con el micrófono |
| Latencia | +90 ms y −0,5 beat fijos | `AcousticLatency` = 0 por defecto, configurable | Regla del proyecto: sin compensaciones sin medición |
| Cuándo se evalúa | Al procesar el cuadro gráfico | Cuando la posición pasa el instante del beat + `Settling` | Independiente de los FPS |
| Qué muestra se usa | La más reciente del búfer | La ventana de análisis que cubre el instante del beat, de centro más cercano; empate, la anterior; hueco: el beat no suma | Determinista; ver `BeatSampler` |
| Aritmética | Acumula reales y suma `LineBonus × perfección` en coma flotante | Pesos enteros y `decimal`; `Línea = 1000 × Σperfección / frases` | Evita errores de acumulación: cantar perfecto da exactamente 10.000 |
| Puntos de la frase | Los ganados desde el fin de la frase anterior | Los de las notas de esa frase | Equivalente salvo frases solapadas |
| Notas candidatas | Frases `actual − 1` y `actual` | Todas las notas de la voz por beat | Misma salida; no depende del estado de pantalla |
| Nivel | Por jugador (`PlayerLevel`) | Un selector global; solo con la canción detenida | Un solo jugador por ahora |
| Reinicio | Al empezar la canción | `Reset` al detener; retroceso de posición > 250 ms; objeto nuevo por canción o dificultad | Definido explícitamente |

Notas solapadas: cada nota se puntúa por separado contra la misma muestra (igual que WorldParty), de modo
que un cantante no puede acertar dos notas simultáneas de tonos distintos. En la biblioteca del usuario 7 de
las primeras 147 canciones tienen solapes; en las otras 140, cantar perfecto da exactamente 10.000.

La paridad con WorldParty no se asume: se mide con un corpus de comparación.

## 10. Visualización de la voz del jugador en WorldParty

Leído en `src/base/UDraw.pas` (`SingDrawPlayerLine`) y `UNote.pas` (`NewNote`). El jugador **no ve el tono
crudo del micrófono**, sino el resultado de la evaluación por beat:

- **Una barra por tramo de beats consecutivos con el mismo tono.** Cada beat evaluado agrega o extiende una
  "nota del jugador" (inicio, largo, tono). Si el beat siguiente cae en el mismo tono y es contiguo, se
  extiende la barra; si no, empieza otra. El tono es un semitono entero, sin centésimas.
- **Si el beat es acierto, el tono se ajusta al de la nota** (la barra queda exactamente sobre la nota,
  a altura completa). Si no acierta, se dibuja en el tono real, plegado a ±6 semitonos de la nota, con
  **65 % de la altura**. Por eso se ve "más o menos bien, arriba o abajo" y nunca una octava de distancia.
- **Solo se dibuja sobre las notas** (no freestyle): entre notas, en silencio o sobre freestyle no hay barra.
- **Se limpia al cambiar de frase**: solo quedan las barras de la frase actual.
- La barra crece con el beat de detección (que ya lleva el retraso de 90 ms + 0,5 beat).
- Extremos redondeados (textura izquierda, media repetida y derecha) y color por jugador; las notas rap
  tienen textura propia.

Diferencia con la estela actual de Kantor: se dibuja una marca por cada análisis de YIN (cada ~21 ms),
con el tono fraccionario y plegado a la octava del carril, también entre notas; por eso se ve la oscilación
del detector.

### Adaptación en Kantor
- Mismo criterio de barras, altura (100 % / 65 %), tono mostrado (el de la nota si acierta; si no, el real
  a ±6) y "solo sobre las notas". El rap se dibuja a media altura del carril, como sus notas.
- **Difiere** en que el escenario de Kantor se desplaza (no pagina por frase), así que no se limpian las
  barras al cambiar de frase: se muestran las de la ventana visible (2 s hacia atrás).
- El modo "Cruda" (cada análisis de YIN) se conserva como ayuda para depurar el micrófono.
