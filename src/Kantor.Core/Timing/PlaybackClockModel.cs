// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Timing;

/// <summary>
/// Convierte lecturas del reloj del dispositivo de salida en posición de la canción y extrapola una
/// lectura (posición, QPC) a otro instante de la base de tiempo común. No toca hardware: el reproductor
/// le pasa las lecturas.
/// </summary>
/// <remarks>
/// El reloj de WASAPI (IAudioClock) cuenta solo audio efectivamente reproducido. Al pausar, el búfer que
/// ya estaba cargado sigue sonando hasta vaciarse y recién entonces el reloj se detiene; al reanudar
/// continúa desde ahí. Por eso la posición de la canción es siempre la lectura cruda del reloj: descontar
/// el avance posterior a la pausa atrasaba la imagen respecto del audio una vez por ciclo.
/// </remarks>
public sealed class PlaybackClockModel
{
    private bool _running;
    private bool _paused;

    public bool IsRunning => _running && !_paused;

    public bool IsPaused => _paused;

    /// <summary>Empieza un flujo nuevo: el reloj del dispositivo arranca en cero junto con la canción.</summary>
    public void Start()
    {
        _running = true;
        _paused = false;
    }

    public void Pause()
    {
        if (_running)
        {
            _paused = true;
        }
    }

    public void Resume()
    {
        _paused = false;
    }

    public void Stop()
    {
        _running = false;
        _paused = false;
    }

    /// <summary>Posición de la canción para una lectura cruda del reloj del dispositivo.</summary>
    public TimeSpan Position(double rawSeconds) =>
        _running ? TimeSpan.FromSeconds(rawSeconds) : TimeSpan.Zero;

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
        return TimeSpan.FromSeconds(rawSeconds + offset);
    }
}
