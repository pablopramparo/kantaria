namespace Kantor.Core.Timing;

/// <summary>
/// Convierte lecturas del reloj del dispositivo de salida en posición de la canción, descontando
/// el tiempo que el reloj siga avanzando durante una pausa, y extrapola una lectura (posición, QPC)
/// a otro instante de la base de tiempo común. No toca hardware: el reproductor le pasa las lecturas.
/// </summary>
public sealed class PlaybackClockModel
{
    private bool _running;
    private bool _paused;
    private double _pausedAt;
    private double _skipped;

    public bool IsRunning => _running && !_paused;

    public bool IsPaused => _paused;

    /// <summary>Empieza un flujo nuevo: el reloj del dispositivo arranca en cero junto con la canción.</summary>
    public void Start()
    {
        _running = true;
        _paused = false;
        _skipped = 0;
        _pausedAt = 0;
    }

    public void Pause(double rawSeconds)
    {
        if (!_running || _paused)
        {
            return;
        }

        _pausedAt = rawSeconds - _skipped;
        _paused = true;
    }

    public void Resume(double rawSeconds)
    {
        if (!_paused)
        {
            return;
        }

        // El reloj del dispositivo puede haber seguido avanzando durante la pausa; ese tiempo no cuenta.
        _skipped = Math.Max(0, rawSeconds - _pausedAt);
        _paused = false;
    }

    public void Stop()
    {
        _running = false;
        _paused = false;
        _skipped = 0;
        _pausedAt = 0;
    }

    /// <summary>Posición de la canción para una lectura cruda del reloj del dispositivo.</summary>
    public TimeSpan Position(double rawSeconds)
    {
        if (!_running)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(_paused ? _pausedAt : rawSeconds - _skipped);
    }

    /// <summary>
    /// Posición de la canción en <paramref name="targetHostTicks"/>, a partir de una lectura del reloj
    /// del dispositivo tomada en <paramref name="observedHostTicks"/>. Null si no está sonando.
    /// </summary>
    public TimeSpan? PositionAt(double rawSeconds, long observedHostTicks, long targetHostTicks)
    {
        if (!IsRunning)
        {
            return null;
        }

        var offset = HostClock.ToSeconds(targetHostTicks - observedHostTicks);
        return TimeSpan.FromSeconds(rawSeconds - _skipped + offset);
    }
}
