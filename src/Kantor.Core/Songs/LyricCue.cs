// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kantor.Core.Songs;

/// <summary>
/// Cálculos del indicador de "ahora te toca" de la letra: un marcador que se acerca al inicio de la frase que
/// viene y, una vez empezada, el avance de la frase cantada. Sin dependencias de WPF.
/// </summary>
public static class LyricCue
{
    /// <summary>Segundos antes del inicio de la frase en los que aparece el marcador.</summary>
    public const double WindowSeconds = 2.0;

    /// <summary>
    /// Avance del marcador hacia el inicio de la frase: 0 cuando recién aparece, 1 cuando toca empezar.
    /// <c>null</c> si todavía falta más que la ventana o la frase ya empezó.
    /// </summary>
    public static double? Approach(double secondsToStart, double windowSeconds = WindowSeconds)
    {
        if (windowSeconds <= 0 || secondsToStart <= 0 || secondsToStart > windowSeconds)
        {
            return null;
        }

        return 1.0 - secondsToStart / windowSeconds;
    }

    /// <summary>
    /// Fracción cantada de la frase entre su primer y último beat (0 antes de empezar, 1 al terminar).
    /// </summary>
    public static double Sung(double beat, double startBeat, double endBeat)
    {
        if (endBeat <= startBeat)
        {
            return beat >= startBeat ? 1.0 : 0.0;
        }

        return Math.Clamp((beat - startBeat) / (endBeat - startBeat), 0.0, 1.0);
    }

    /// <summary>
    /// Índice de la frase que viene y todavía no empezó: la primera si aún no empezó ninguna, o la siguiente a
    /// la que se está mostrando. <c>-1</c> si no hay.
    /// </summary>
    public static int UpcomingIndex(Voice voice, double beat)
    {
        if (voice.Lines.Count == 0)
        {
            return -1;
        }

        var current = voice.LineIndexAt(beat);
        if (current == 0 && beat < voice.Lines[0].StartBeat)
        {
            return 0;
        }

        var next = current + 1;
        return next < voice.Lines.Count && beat < voice.Lines[next].StartBeat ? next : -1;
    }

    /// <summary>Un tramo horizontal de una sílaba dentro de su frase (unidades de dibujo).</summary>
    public readonly record struct Span(double Start, double End);

    /// <summary>
    /// Frase que se muestra arriba: la que se está cantando o, si ya terminó (con <paramref name="holdBeats"/> de
    /// margen), la que viene. Así el indicador vive siempre en la fila de arriba y no salta a la de abajo.
    /// </summary>
    public static int DisplayIndex(Voice voice, double beat, double holdBeats)
    {
        var index = voice.LineIndexAt(beat);
        if (index < 0)
        {
            return -1;
        }

        if (beat > voice.Lines[index].EndBeat + holdBeats && index + 1 < voice.Lines.Count)
        {
            return index + 1;
        }

        return index;
    }

    /// <summary>
    /// Posición horizontal hasta donde la frase ya fue cantada, siguiendo el tiempo real de cada sílaba: entera
    /// si ya terminó, proporcional dentro de la que suena y detenida en las pausas entre sílabas.
    /// <paramref name="notes"/> y <paramref name="spans"/> van en el mismo orden; un tramo vacío (sílaba sin
    /// texto) no adelanta el relleno.
    /// </summary>
    public static double FillX(IReadOnlyList<(double StartBeat, double EndBeat)> notes, IReadOnlyList<Span> spans,
        double beat, double textStart)
    {
        var x = textStart;
        var count = Math.Min(notes.Count, spans.Count);
        for (var i = 0; i < count; i++)
        {
            var (start, end) = notes[i];
            var span = spans[i];
            if (span.End <= span.Start)
            {
                continue;
            }

            if (beat >= end)
            {
                x = span.End;
                continue;
            }

            if (beat > start && end > start)
            {
                return span.Start + (span.End - span.Start) * (beat - start) / (end - start);
            }

            return Math.Max(x, Math.Min(x, span.Start));
        }

        return x;
    }
}
