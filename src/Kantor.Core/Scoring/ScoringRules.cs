// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Scoring;

/// <summary>Nivel de dificultad: define cuántos semitonos de error se toleran en una nota normal o dorada.</summary>
public enum Difficulty
{
    Easy = 0,
    Medium = 1,
    Hard = 2,
}

public static class DifficultyExtensions
{
    /// <summary>Tolerancia en semitonos: Fácil 2, Medio 1, Difícil 0.</summary>
    public static int ToleranceSemitones(this Difficulty difficulty) => 2 - (int)difficulty;
}

/// <summary>Parámetros de la puntuación. Son inmutables: cambiar uno implica puntuar de nuevo desde el principio.</summary>
public sealed record ScoringRules
{
    /// <summary>
    /// Versión del sistema de puntuación que se guarda con cada interpretación del historial. Subirla cuando una
    /// regla cambie de modo que los puntajes ya no sean comparables.
    /// </summary>
    public const int ScoringVersion = 1;

    /// <summary>Máximo de la canción.</summary>
    public const int MaxSongPoints = 10_000;

    /// <summary>Parte del máximo que reparte el bono de línea cuando está activo.</summary>
    public const int LineBonusPoints = 1_000;

    public Difficulty Difficulty { get; init; } = Difficulty.Easy;

    public bool LineBonus { get; init; } = true;

    /// <summary>
    /// Nivel RMS mínimo (dB) para que haya "sonido": rige para el rap y como compuerta de las notas con tono.
    /// Provisional y sin equivalencia con el umbral de WorldParty. El detector de tono ya descarta por debajo
    /// de <c>PitchTracker.DefaultGateDb</c>, así que en las notas con tono no tiene efecto por debajo de ese valor.
    /// </summary>
    public double LevelGateDb { get; init; } = -50;

    /// <summary>
    /// Latencia acústica: cuánto después de lo que se escucha de la canción llega al micrófono lo que canta
    /// el jugador. Desplaza el instante de la voz que se compara con cada beat. Cero hasta que se mida.
    /// </summary>
    public TimeSpan AcousticLatency { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Tiempo de asentamiento: cuánto esperar, después del instante de un beat, para dar por llegadas las
    /// muestras de tono que lo cubren (ventana de análisis más retardo de captura y proceso). No corre el
    /// instante comparado: solo decide cuándo se evalúa el beat. Si una muestra llega más tarde, se ignora.
    /// </summary>
    public TimeSpan Settling { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Puente de huecos: separación máxima entre dos cuadros con tono para considerar que el hueco entre
    /// ambos (consonantes, respiración, transiciones) es parte de la misma frase. En ese hueco, y solo si hay
    /// sonido, se evalúa con el tono vecino más cercano. 150 ms equivalen a ~120 ms sin tono con cuadros
    /// cada ~30 ms. Cero lo desactiva. No extiende finales de nota. Los beats dentro de un hueco esperan
    /// hasta <c>GapBridge + Settling</c> para ver el tono posterior.
    /// </summary>
    public TimeSpan GapBridge { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Retroceso de la posición a partir del cual se considera que la canción volvió a empezar.</summary>
    public TimeSpan RewindThreshold { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Puntos que reparten las notas: 9000 con bono de línea, 10000 sin él.</summary>
    public int NotePoints => LineBonus ? MaxSongPoints - LineBonusPoints : MaxSongPoints;
}
