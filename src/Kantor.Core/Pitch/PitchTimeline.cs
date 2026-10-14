namespace Kantor.Core.Pitch;

/// <summary>
/// Un análisis de tono ubicado en la canción: <see cref="Time"/> es el instante central de las
/// muestras analizadas, no el momento en que terminó el cálculo.
/// </summary>
public readonly record struct PitchSample(
    TimeSpan Time,
    TimeSpan WindowLength,
    double Frequency,
    double Confidence,
    double LevelDb)
{
    public bool IsVoiced => Frequency > 0;

    public TimeSpan Start => Time - WindowLength / 2;

    public TimeSpan End => Time + WindowLength / 2;
}

/// <summary>
/// Historial reciente de tonos de una entrada, ordenado por tiempo de canción. Lo escribe el hilo
/// de captura y lo lee el escenario (y, más adelante, la puntuación). Es seguro entre hilos.
/// Si el tiempo retrocede (la canción se reinició), el historial se descarta.
/// </summary>
public sealed class PitchTimeline
{
    private readonly object _gate = new();
    private readonly PitchSample[] _items;
    private int _start;
    private int _count;

    public PitchTimeline(int capacity = 2048)
    {
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _items = new PitchSample[capacity];
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Add(PitchSample sample)
    {
        lock (_gate)
        {
            if (_count > 0 && sample.Time < At(_count - 1).Time)
            {
                _start = 0;
                _count = 0;
            }

            if (_count == _items.Length)
            {
                _start = (_start + 1) % _items.Length;
                _count--;
            }

            _items[(_start + _count) % _items.Length] = sample;
            _count++;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _start = 0;
            _count = 0;
        }
    }

    /// <summary>Agrega a <paramref name="into"/> las muestras con <c>Time</c> en [from, to], en orden.</summary>
    public void CopyRange(TimeSpan from, TimeSpan to, List<PitchSample> into)
    {
        lock (_gate)
        {
            for (var i = 0; i < _count; i++)
            {
                var item = At(i);
                if (item.Time > to)
                {
                    break;
                }

                if (item.Time >= from)
                {
                    into.Add(item);
                }
            }
        }
    }

    private PitchSample At(int index) => _items[(_start + index) % _items.Length];
}
