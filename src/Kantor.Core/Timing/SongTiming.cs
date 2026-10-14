namespace Kantor.Core.Timing;

/// <summary>
/// Posición de reproducción de la canción. Es la única referencia temporal que consumen el escenario
/// y, más adelante, la captura y la puntuación; quien la implementa decide cómo obtenerla
/// (dispositivo de audio, compensación de latencia, etc.).
/// </summary>
public interface ISongClock
{
    /// <summary>Posición en el archivo de audio, tal como se está escuchando.</summary>
    TimeSpan Position { get; }

    bool IsPlaying { get; }
}

/// <summary>Conversión entre tiempo de audio y beats de UltraStar (un beat = un cuarto del BPM del archivo).</summary>
public readonly struct SongTiming
{
    private readonly double _beatsPerSecond;
    private readonly double _gapSeconds;

    public SongTiming(double bpm, double gapMilliseconds)
    {
        if (bpm <= 0 || double.IsNaN(bpm) || double.IsInfinity(bpm))
        {
            throw new ArgumentOutOfRangeException(nameof(bpm), "El BPM debe ser positivo.");
        }

        _beatsPerSecond = bpm * 4.0 / 60.0;
        _gapSeconds = gapMilliseconds / 1000.0;
    }

    public double BeatAt(TimeSpan position) => (position.TotalSeconds - _gapSeconds) * _beatsPerSecond;

    public TimeSpan TimeOf(double beat) => TimeSpan.FromSeconds(_gapSeconds + beat / _beatsPerSecond);
}
