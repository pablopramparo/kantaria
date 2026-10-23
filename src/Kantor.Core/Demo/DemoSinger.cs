// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Songs;
using Kantor.Core.Timing;

namespace Kantor.Core.Demo;

/// <summary>
/// Un cantante simulado para el modo demo (capturas, certificación): dada la voz de la canción y la posición, dice
/// qué tono y nivel entregaría un micrófono. Canta cada nota con un atraso de reacción (como una persona), acierta
/// una proporción fija de las notas y en el resto se equivoca por unos semitonos. Es determinista: la misma canción y
/// la misma semilla dan siempre la misma "interpretación".
/// </summary>
public sealed class DemoSinger
{
    /// <summary>Nivel de la voz simulada, por encima de la compuerta de −50 dBFS.</summary>
    public const double VoicedLevelDb = -22;

    public const double SilenceLevelDb = -80;

    /// <summary>Al empezar cada nota hay un instante sin tono (la consonante), como en una voz real.</summary>
    private static readonly TimeSpan Onset = TimeSpan.FromMilliseconds(25);

    private readonly double _accuracy;
    private readonly TimeSpan _reaction;
    private readonly int _seed;

    /// <param name="accuracy">Proporción de notas que canta afinadas (0 a 1).</param>
    /// <param name="reaction">Cuánto después de la nota suena la voz: el retardo que la puntuación compensa.</param>
    /// <param name="seed">Distingue a un cantante de otro.</param>
    public DemoSinger(double accuracy, TimeSpan reaction, int seed)
    {
        _accuracy = Math.Clamp(accuracy, 0, 1);
        _reaction = reaction;
        _seed = seed;
    }

    /// <summary>Precisión por omisión de cada puesto: el primero canta muy bien, los demás peor.</summary>
    public static double DefaultAccuracy(int slot) => slot switch
    {
        0 => 0.96,
        1 => 0.72,
        2 => 0.85,
        _ => 0.60,
    };

    /// <summary>Frecuencia (0 = sin tono) y nivel que entrega el micrófono cuando la canción va por <paramref name="position"/>.</summary>
    public (double Frequency, double LevelDb) Sing(Voice voice, SongTiming timing, TimeSpan position)
    {
        var heard = position - _reaction;
        var beat = timing.BeatAt(heard);
        var note = FindNote(voice, beat);
        if (note is not { } n)
        {
            return (0, SilenceLevelDb);
        }

        if (heard - timing.TimeOf(n.StartBeat) < Onset)
        {
            return (0, SilenceLevelDb);
        }

        var tone = (double)n.Tone;
        if (Roll(n.StartBeat) >= _accuracy)
        {
            tone += WrongBy(n.StartBeat);
        }

        // vibrato leve, de ±15 centésimas a 5,5 Hz
        tone += 0.15 * Math.Sin(2 * Math.PI * 5.5 * heard.TotalSeconds);

        // Como la puntuación acepta cualquier octava, se canta siempre en un registro cómodo (do3 a do5).
        var midi = tone + 60;
        midi -= 12 * Math.Floor((midi - 48) / 12);
        return (440 * Math.Pow(2, (midi - 69) / 12), VoicedLevelDb);
    }

    private static Note? FindNote(Voice voice, double beat)
    {
        foreach (var line in voice.Lines)
        {
            if (beat < line.StartBeat || beat >= line.EndBeat)
            {
                continue;
            }

            foreach (var note in line.Notes)
            {
                if (note.IsScored && beat >= note.StartBeat && beat < note.EndBeat)
                {
                    return note;
                }
            }
        }

        return null;
    }

    /// <summary>Número en [0, 1) que depende solo de la semilla y de la nota.</summary>
    private double Roll(int startBeat) => Hash(startBeat, 1) / (double)0x1_0000_0000;

    /// <summary>Error de 3 a 5 semitonos, para arriba o para abajo, fijo para cada nota.</summary>
    private double WrongBy(int startBeat)
    {
        var h = Hash(startBeat, 2);
        var size = 3 + (int)(h % 3);
        return (h & 8) == 0 ? size : -size;
    }

    private uint Hash(int startBeat, int salt)
    {
        unchecked
        {
            var h = (uint)_seed * 2654435761u + (uint)startBeat * 40503u + (uint)salt * 97u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            h *= 3266489917u;
            h ^= h >> 16;
            return h;
        }
    }
}
