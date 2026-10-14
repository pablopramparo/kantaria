// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Pitch;
using Kantor.Core.Songs;
using Kantor.Core.Timing;

namespace Kantor.Core.Scoring;

/// <summary>Estado de la puntuación en un momento. Los enteros están redondeados a múltiplos de 10.</summary>
public readonly record struct ScoreSnapshot(
    decimal Notes,
    decimal Golden,
    decimal Lines,
    int NotesRounded,
    int GoldenRounded,
    int LinesRounded,
    int BeatsEvaluated,
    int BeatsHit,
    int BeatsTotal,
    int LinesCompleted)
{
    public int Total => NotesRounded + GoldenRounded + LinesRounded;

    public static ScoreSnapshot Empty(int beatsTotal) => new(0, 0, 0, 0, 0, 0, 0, 0, beatsTotal, 0);
}

/// <summary>
/// Lo que se ve de un beat evaluado con sonido: tono mostrado (el de la nota si acertó; si no, el real a ±6
/// semitonos de la nota) y si acertó. Los beats sin sonido o sin voz detectada no generan resultado.
/// </summary>
public readonly record struct BeatResult(int Beat, NoteKind Kind, int DisplayTone, bool Hit);

/// <summary>
/// Puntúa una voz contra el historial de tono de una entrada. No conoce el origen del tono (YIN, WASAPI)
/// ni el dibujo: consume la voz, la conversión beat/tiempo y un <see cref="PitchTimeline"/>.
/// </summary>
/// <remarks>
/// Ciclo de vida, explícito:
/// <list type="bullet">
/// <item><description><b>Pausa</b>: no hace nada. La posición queda quieta, no hay beats nuevos y el puntaje se conserva.</description></item>
/// <item><description><b>Detener / empezar de nuevo</b>: quien controla la reproducción llama <see cref="Reset"/>. Si no
/// lo hace, un retroceso de posición mayor que <see cref="ScoringRules.RewindThreshold"/> se toma como reinicio.</description></item>
/// <item><description><b>Canción nueva o reglas nuevas</b>: se crea otro scorer.</description></item>
/// <item><description><b>Salto hacia adelante</b>: los beats saltados se evalúan igual con lo que haya en el historial;
/// sin muestras que los cubran, no suman.</description></item>
/// <item><description><b>Fin natural</b>: el puntaje queda como está hasta que alguien llame a <see cref="Reset"/> o la
/// posición vuelva a empezar.</description></item>
/// </list>
/// Un beat se evalúa una sola vez, en orden, cuando la posición alcanza su instante más el tiempo de
/// asentamiento. El resultado no depende de cuántas veces ni cada cuánto se llame a <see cref="Advance"/>.
/// </remarks>
public sealed class VoiceScorer
{
    private readonly record struct ScoredBeat(int Beat, NoteKind Kind, int Tone, int Line);

    private readonly SongTiming _timing;
    private readonly ScoredBeat[] _beats;
    private readonly int[] _lineValue;
    private readonly int[] _lineBeats;
    private readonly int _scoreValue;
    private readonly int _scoredLines;
    private readonly List<PitchSample> _scratch = [];
    private readonly List<BeatResult> _results = [];

    private int _cursor;
    private TimeSpan _lastPosition = TimeSpan.MinValue;
    private int _normalWeight;
    private int _goldenWeight;
    private int _beatsHit;
    private int _linesCompleted;
    private decimal _perfectionSum;
    private int[] _lineHitWeight;
    private int[] _lineRemaining;

    public VoiceScorer(Voice voice, SongTiming timing, ScoringRules? rules = null)
    {
        Rules = rules ?? new ScoringRules();
        _timing = timing;

        var beats = new List<ScoredBeat>();
        _lineValue = new int[voice.Lines.Count];
        _lineBeats = new int[voice.Lines.Count];
        for (var l = 0; l < voice.Lines.Count; l++)
        {
            foreach (var note in voice.Lines[l].Notes)
            {
                var factor = NoteScoring.Factor(note.Kind);
                if (factor == 0 || note.Length <= 0)
                {
                    continue;
                }

                _lineValue[l] += note.Length * factor;
                _lineBeats[l] += note.Length;
                for (var b = note.StartBeat; b < note.EndBeat; b++)
                {
                    beats.Add(new ScoredBeat(b, note.Kind, note.Tone, l));
                }
            }

            _scoreValue += _lineValue[l];
            if (_lineValue[l] > 0)
            {
                _scoredLines++;
            }
        }

        // Estable: ante notas solapadas, cada una conserva sus beats en el orden del archivo.
        _beats = beats.OrderBy(b => b.Beat).ToArray();
        _lineHitWeight = new int[voice.Lines.Count];
        _lineRemaining = [.. _lineBeats];
    }

    public ScoringRules Rules { get; }

    /// <summary>Cantidad de beats puntuables de la voz.</summary>
    public int BeatsTotal => _beats.Length;

    /// <summary>
    /// Resultados visibles de los beats ya evaluados, en orden. Se vacía con <see cref="Reset"/>. Es lo que
    /// consume el dibujo: el puntaje y lo que se ve salen de la misma evaluación. Solo para el hilo que
    /// llama a <see cref="Advance"/>.
    /// </summary>
    public IReadOnlyList<BeatResult> Results => _results;

    public ScoreSnapshot Snapshot
    {
        get
        {
            if (_scoreValue == 0)
            {
                return ScoreSnapshot.Empty(_beats.Length) with { BeatsEvaluated = _cursor, LinesCompleted = _linesCompleted };
            }

            var notePoints = (decimal)Rules.NotePoints;
            var notes = notePoints * _normalWeight / _scoreValue;
            var golden = notePoints * _goldenWeight / _scoreValue;
            var lines = Rules.LineBonus && _scoredLines > 0
                ? ScoringRules.LineBonusPoints * _perfectionSum / _scoredLines
                : 0m;

            // Normal y dorada se redondean en sentidos opuestos para que el máximo sea 10000 y no 10010.
            var notesRounded = (int)(Math.Round(notes / 10, MidpointRounding.ToEven) * 10);
            var goldenRounded = notesRounded < notes
                ? (int)(Math.Ceiling(golden / 10) * 10)
                : (int)(Math.Floor(golden / 10) * 10);
            var linesRounded = (int)(Math.Floor(lines / 10) * 10);

            return new ScoreSnapshot(notes, golden, lines, notesRounded, goldenRounded, linesRounded,
                _cursor, _beatsHit, _beats.Length, _linesCompleted);
        }
    }

    /// <summary>
    /// Resultado provisional de los beats que todavía no se evaluaron pero para los que ya llegó la voz: solo
    /// para dibujar, así la barra aparece sin esperar el tiempo de asentamiento ni el posible hueco. No cambia
    /// el estado ni el puntaje; el resultado definitivo puede diferir (por ejemplo, si un hueco se puentea).
    /// </summary>
    /// <param name="timeline">Historial de tono de la entrada.</param>
    /// <param name="position">Posición actual de la canción.</param>
    public List<BeatResult> Preview(PitchTimeline timeline, TimeSpan position)
    {
        var preview = new List<BeatResult>();
        for (var i = _cursor; i < _beats.Length; i++)
        {
            var beat = _beats[i];
            var instant = _timing.TimeOf(beat.Beat + 0.5) + Rules.AcousticLatency;
            if (instant > position)
            {
                break;
            }

            var sample = BeatSampler.Select(instant, timeline, _scratch);

            // Solo se decide con las ventanas que cubren el instante ya completas: si el análisis más reciente
            // todavía no pasó el instante, otro de centro más cercano podría estar por llegar.
            var newest = TimeSpan.MinValue;
            foreach (var candidate in _scratch)
            {
                if (candidate.Time > newest)
                {
                    newest = candidate.Time;
                }
            }

            if (newest < instant + PreviewMargin)
            {
                break;
            }

            if (sample is { } s && NoteScoring.Evaluate(beat.Kind, beat.Tone, s, Rules) is { } shown)
            {
                preview.Add(new BeatResult(beat.Beat, beat.Kind, shown.DisplayTone, shown.Hit));
            }
        }

        return preview;
    }

    /// <summary>Cuánto después del instante de un beat debe haber llegado un análisis para dar por completo su entorno.</summary>
    private static readonly TimeSpan PreviewMargin = TimeSpan.FromMilliseconds(20);

    /// <summary>Vuelve a cero: la canción empieza de nuevo.</summary>
    public void Reset()
    {
        _cursor = 0;
        _lastPosition = TimeSpan.MinValue;
        _normalWeight = 0;
        _goldenWeight = 0;
        _beatsHit = 0;
        _linesCompleted = 0;
        _perfectionSum = 0;
        _results.Clear();
        Array.Clear(_lineHitWeight);
        _lineRemaining = [.. _lineBeats];
    }

    /// <summary>Evalúa los beats cuyo instante, más el asentamiento, ya pasó respecto de <paramref name="position"/>.</summary>
    public void Advance(TimeSpan position, PitchTimeline timeline)
    {
        if (_lastPosition != TimeSpan.MinValue && position < _lastPosition - Rules.RewindThreshold)
        {
            Reset();
        }

        if (position > _lastPosition)
        {
            _lastPosition = position;
        }

        while (_cursor < _beats.Length)
        {
            var beat = _beats[_cursor];
            var instant = _timing.TimeOf(beat.Beat + 0.5) + Rules.AcousticLatency;
            if (instant + Rules.Settling > position)
            {
                break;
            }

            var sample = BeatSampler.Select(instant, timeline, _scratch);

            // Hueco corto dentro de una frase: sonido pero sin tono, con tono a ambos lados. Se evalúa con el
            // vecino más cercano; si el posterior todavía puede llegar, se espera (solo estos beats esperan).
            if (sample is { IsVoiced: false } silent && silent.LevelDb >= Rules.LevelGateDb
                && Rules.GapBridge > TimeSpan.Zero && !NoteScoring.IsRap(beat.Kind))
            {
                var bridge = BeatSampler.Bridge(instant, _scratch, Rules.LevelGateDb, Rules.GapBridge, out var mayBridgeLater);
                if (mayBridgeLater && instant + Rules.GapBridge + Rules.Settling > position)
                {
                    break;
                }

                sample = bridge ?? sample;
            }

            var evaluation = sample is { } s ? NoteScoring.Evaluate(beat.Kind, beat.Tone, s, Rules) : null;
            if (evaluation is { } shown)
            {
                _results.Add(new BeatResult(beat.Beat, beat.Kind, shown.DisplayTone, shown.Hit));
            }

            if (evaluation is { Hit: true })
            {
                var factor = NoteScoring.Factor(beat.Kind);
                if (NoteScoring.IsGolden(beat.Kind))
                {
                    _goldenWeight += factor;
                }
                else
                {
                    _normalWeight += factor;
                }

                _lineHitWeight[beat.Line] += factor;
                _beatsHit++;
            }

            _cursor++;
            if (--_lineRemaining[beat.Line] == 0)
            {
                CompleteLine(beat.Line);
            }
        }
    }

    private void CompleteLine(int line)
    {
        _linesCompleted++;
        if (_lineValue[line] == 0 || _scoreValue == 0)
        {
            return;
        }

        // Perfección = puntos de la frase / (máximo de la frase - 2), entre 0 y 1; los 2 puntos de holgura
        // evitan exigir una frase perfecta. Frases de máximo muy bajo cuentan como perfectas.
        var notePoints = (decimal)Rules.NotePoints;
        var maxLine = notePoints * _lineValue[line] / _scoreValue;
        var lineScore = notePoints * _lineHitWeight[line] / _scoreValue;
        var perfection = maxLine <= 2 ? 1m : Math.Clamp(lineScore / (maxLine - 2), 0m, 1m);
        _perfectionSum += perfection;
    }
}
