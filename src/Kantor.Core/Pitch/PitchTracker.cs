using Kantor.Core.Audio;
using Kantor.Core.Timing;

namespace Kantor.Core.Pitch;

/// <summary>
/// Resultado de un análisis. <see cref="WindowStartHost"/> y <see cref="WindowEndHost"/> delimitan,
/// en la base de tiempo común (<see cref="HostClock"/>), las muestras que se analizaron.
/// </summary>
public readonly record struct PitchFrame(
    double Frequency,
    double Confidence,
    double LevelDb,
    long WindowStartHost,
    long WindowEndHost)
{
    public bool IsVoiced => Frequency > 0;

    /// <summary>Instante central de la ventana: a lo que corresponde el tono estimado.</summary>
    public long CenterHost => WindowStartHost + (WindowEndHost - WindowStartHost) / 2;

    public static PitchFrame Silent => new(0, 0, LevelReading.FloorDb, 0, 0);
}

/// <summary>
/// Acumula audio mono de un canal y emite un <see cref="PitchFrame"/> cada <c>hop</c> muestras.
/// Por debajo de <c>gateDb</c> se considera silencio y no se intenta detectar tono.
/// Cada paquete llega con la marca de tiempo de su primera muestra; la ventana analizada termina
/// en la última muestra del paquete, así que su instante sale del reloj del dispositivo y no del
/// momento en que se procesa.
/// </summary>
public sealed class PitchTracker
{
    public const double DefaultGateDb = -50;

    private readonly int _sampleRate;
    private readonly PitchDetector _detector;
    private readonly float[] _window;
    private readonly int _hop;
    private readonly double _gateDb;
    private int _sinceLastAnalysis;
    private int _filled;

    public PitchTracker(int sampleRate, double gateDb = DefaultGateDb, int hop = 0)
    {
        _sampleRate = sampleRate;
        _detector = new PitchDetector(sampleRate);
        _window = new float[_detector.WindowSize];
        _hop = hop > 0 ? hop : _detector.WindowSize / 2;
        _gateDb = gateDb;
    }

    public int WindowSize => _window.Length;

    public TimeSpan WindowDuration => TimeSpan.FromSeconds(_window.Length / (double)_sampleRate);

    /// <summary>Descarta lo acumulado; hay que llamarlo si el flujo tuvo una discontinuidad.</summary>
    public void Reset()
    {
        _filled = 0;
        _sinceLastAnalysis = 0;
    }

    /// <summary>
    /// Incorpora un paquete cuya primera muestra ocurrió en <paramref name="packetStartHost"/> y
    /// devuelve el análisis si se cumplió el salto. Cuando un paquete trae más de un salto, se
    /// analiza una sola vez con la ventana más reciente.
    /// </summary>
    public bool Push(ReadOnlySpan<float> samples, long packetStartHost, out PitchFrame frame)
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

        var end = packetStartHost + SamplesToHostTicks(samples.Length);
        var start = end - SamplesToHostTicks(_window.Length);
        var level = LevelMeter.Measure(_window).RmsDb;
        if (level < _gateDb)
        {
            frame = new PitchFrame(0, 0, level, start, end);
            return true;
        }

        var estimate = _detector.Detect(_window);
        frame = new PitchFrame(estimate.Frequency, estimate.Confidence, level, start, end);
        return true;
    }

    private long SamplesToHostTicks(int samples) =>
        (long)Math.Round(samples * (double)HostClock.TicksPerSecond / _sampleRate);
}
