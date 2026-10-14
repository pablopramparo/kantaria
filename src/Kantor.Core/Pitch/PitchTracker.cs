using Kantor.Core.Audio;

namespace Kantor.Core.Pitch;

/// <summary>Resultado de un análisis: nivel de la ventana y, si hay voz, su frecuencia.</summary>
public readonly record struct PitchFrame(double Frequency, double Confidence, double LevelDb)
{
    public bool IsVoiced => Frequency > 0;

    public static PitchFrame Silent => new(0, 0, LevelReading.FloorDb);
}

/// <summary>
/// Acumula audio mono de un canal y emite un <see cref="PitchFrame"/> cada <c>hop</c> muestras.
/// Por debajo de <c>gateDb</c> se considera silencio y no se intenta detectar tono.
/// </summary>
public sealed class PitchTracker
{
    public const double DefaultGateDb = -50;

    private readonly PitchDetector _detector;
    private readonly float[] _window;
    private readonly int _hop;
    private readonly double _gateDb;
    private int _sinceLastAnalysis;
    private int _filled;

    public PitchTracker(int sampleRate, double gateDb = DefaultGateDb, int hop = 0)
    {
        _detector = new PitchDetector(sampleRate);
        _window = new float[_detector.WindowSize];
        _hop = hop > 0 ? hop : _detector.WindowSize / 2;
        _gateDb = gateDb;
    }

    public int WindowSize => _window.Length;

    /// <summary>
    /// Incorpora muestras y devuelve el último análisis si se cumplió el salto; si no, <c>false</c>.
    /// Cuando un bloque trae más de un salto, se analiza una sola vez con la ventana más reciente.
    /// </summary>
    public bool Push(ReadOnlySpan<float> samples, out PitchFrame frame)
    {
        if (samples.Length >= _window.Length)
        {
            samples[^_window.Length..].CopyTo(_window);
        }
        else
        {
            Array.Copy(_window, samples.Length, _window, 0, _window.Length - samples.Length);
            samples.CopyTo(_window.AsSpan(_window.Length - samples.Length));
        }

        _filled = Math.Min(_window.Length, _filled + samples.Length);
        _sinceLastAnalysis += samples.Length;

        if (_filled < _window.Length || _sinceLastAnalysis < _hop)
        {
            frame = default;
            return false;
        }

        _sinceLastAnalysis = 0;
        var level = LevelMeter.Measure(_window).RmsDb;
        if (level < _gateDb)
        {
            frame = new PitchFrame(0, 0, level);
            return true;
        }

        var estimate = _detector.Detect(_window);
        frame = new PitchFrame(estimate.Frequency, estimate.Confidence, level);
        return true;
    }
}
