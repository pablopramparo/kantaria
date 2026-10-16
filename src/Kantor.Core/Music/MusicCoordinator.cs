// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Music;

/// <summary>Las dos pistas de la interfaz.</summary>
public enum MusicTrack
{
    /// <summary>"8Bit Title Screen": inicio y navegación.</summary>
    Menu,

    /// <summary>"Heroic Loop": pantalla de resultados.</summary>
    Results,
}

/// <summary>Qué está pasando en Kantor, a efectos de la música de la interfaz.</summary>
public enum MusicScene
{
    /// <summary>Sin música: antes de que la interfaz esté lista, o ya cerrada.</summary>
    None,

    /// <summary>Menú, jugadores, opciones, cola…: suena la música de inicio.</summary>
    Navigation,

    /// <summary>
    /// Lista de canciones: callada, para que se oigan las vistas previas de las canciones. Al volver al menú, la música
    /// sigue donde quedó.
    /// </summary>
    Library,

    /// <summary>Hay una canción sonando o en pausa: no suena nada de la interfaz.</summary>
    Song,

    /// <summary>Pantalla de resultados: suena la melodía de celebración.</summary>
    Results,
}

/// <summary>
/// Una pista en loop con su propia salida (un solo dispositivo por pista, nunca dos para la misma música). Implementada
/// por la aplicación con NAudio; las pruebas usan una falsa. Los métodos se llaman siempre desde el hilo de la interfaz.
/// </summary>
public interface IMusicChannel : IDisposable
{
    /// <summary>El archivo de la pista existe (no garantiza que se pueda reproducir).</summary>
    bool IsAvailable { get; }

    /// <summary>Hay una salida abierta sonando.</summary>
    bool IsPlaying { get; }

    /// <summary>
    /// Abre la salida y empieza. Con <paramref name="fromBeginning"/> parte del principio; si no, sigue donde quedó.
    /// Devuelve <c>false</c> si no pudo (sin dispositivo, archivo ilegible): se reintenta más tarde.
    /// </summary>
    bool Start(bool fromBeginning);

    /// <summary>Volumen final de la pista, de 0 a 1 (ya incluye el volumen de la interfaz y el fundido).</summary>
    void SetGain(float gain);

    /// <summary>Cierra la salida y conserva la posición del loop.</summary>
    void Stop();
}

public interface IMusicOutput : IDisposable
{
    IMusicChannel Channel(MusicTrack track);
}

/// <summary>
/// La única fuente de verdad sobre qué música suena. Recibe la escena (<see cref="SetScene"/>), el volumen del usuario
/// (<see cref="SetVolume"/>) y el paso del tiempo (<see cref="Advance"/>) y decide, pista por pista, el fundido, el
/// arranque y la parada. No sabe de pantallas ni de NAudio: se prueba con canales falsos y tiempo manual.
/// </summary>
/// <remarks>
/// Cada pista tiene un nivel de fundido (0–1) que se mueve a velocidad fija hacia su objetivo; el volumen final es
/// <c>fundido × volumen</c>. Así un cambio de escena a mitad de un fundido continúa desde donde estaba (sin saltos), el
/// volumen del usuario se aplica al instante en cualquier momento y nunca hay dos pistas al máximo a la vez.
/// </remarks>
public sealed class MusicCoordinator : IDisposable
{
    public static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(400);

    /// <summary>Tiempo que debe sostenerse "navegación" antes de volver a la música tras una canción (evita parpadeos).</summary>
    public static readonly TimeSpan NavigationSettle = TimeSpan.FromMilliseconds(150);

    /// <summary>Cada cuánto se reintenta abrir una salida que falló (por ejemplo, un dispositivo que desapareció).</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(2);

    private const float Silent = 0.0005f;

    private sealed class State(IMusicChannel channel)
    {
        public IMusicChannel Channel { get; } = channel;

        /// <summary>Nivel de fundido actual (0 = callada, 1 = a todo volumen).</summary>
        public float Level { get; set; }

        /// <summary>La próxima vez que arranque, hacerlo desde el principio.</summary>
        public bool RestartPending { get; set; }

        public TimeSpan RetryAt { get; set; }
    }

    private readonly IMusicOutput _output;
    private readonly State[] _states;
    private MusicScene _scene = MusicScene.None;
    private MusicScene _requested = MusicScene.None;
    private TimeSpan _navigationHeld;
    private TimeSpan _now;
    private float _volume;
    private bool _disposed;

    public MusicCoordinator(IMusicOutput output, float volume)
    {
        _output = output;
        _volume = Math.Clamp(volume, 0f, 1f);
        _states = [.. Enum.GetValues<MusicTrack>().Select(t => new State(output.Channel(t)))];
    }

    /// <summary>La escena vigente (puede ir detrás de la pedida mientras se sostiene la navegación).</summary>
    public MusicScene Scene => _scene;

    public float Volume => _volume;

    /// <summary>Nivel de fundido de una pista (0–1), para las pruebas.</summary>
    public float LevelOf(MusicTrack track) => _states[(int)track].Level;

    /// <summary>Volumen final que se le pidió a la pista en este momento.</summary>
    public float GainOf(MusicTrack track) => _states[(int)track].Level * _volume;

    /// <summary>Pide una escena. Es idempotente: repetir la misma no hace nada (no reinicia ni vuelve a pedir fundidos).</summary>
    public void SetScene(MusicScene scene)
    {
        if (_disposed)
        {
            return;
        }

        if (scene != _requested)
        {
            _requested = scene;
            _navigationHeld = TimeSpan.Zero;
        }

        if (!(scene == MusicScene.Navigation && _scene == MusicScene.Song))
        {
            Apply(scene); // solo volver al menú desde una canción espera un instante (ver NavigationSettle); el resto es inmediato
        }
    }

    /// <summary>Cambia el volumen del usuario (0–1) y lo aplica ya, también en medio de un fundido.</summary>
    public void SetVolume(float volume)
    {
        if (_disposed)
        {
            return;
        }

        _volume = Math.Clamp(volume, 0f, 1f);
        Drive();
    }

    /// <summary>Hace avanzar el tiempo: mueve los fundidos, arranca y detiene las salidas.</summary>
    public void Advance(TimeSpan elapsed)
    {
        if (_disposed)
        {
            return;
        }

        _now += elapsed;
        if (_requested == MusicScene.Navigation && _scene == MusicScene.Song)
        {
            _navigationHeld += elapsed;
            if (_navigationHeld >= NavigationSettle)
            {
                Apply(MusicScene.Navigation);
            }
        }

        foreach (var track in Enum.GetValues<MusicTrack>())
        {
            var state = _states[(int)track];
            var target = TargetOf(track);
            var step = (float)(elapsed.TotalSeconds / (target > state.Level ? FadeIn : FadeOut).TotalSeconds);
            state.Level = target > state.Level
                ? Math.Min(target, state.Level + step)
                : Math.Max(target, state.Level - step);
        }

        Drive();
    }

    /// <summary>Cierre de la aplicación: detiene y libera todo, sin fundido.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scene = _requested = MusicScene.None;
        foreach (var state in _states)
        {
            state.Level = 0;
            state.Channel.Stop();
            state.Channel.Dispose();
        }

        _output.Dispose();
    }

    private void Apply(MusicScene scene)
    {
        if (scene == _scene)
        {
            return;
        }

        if (scene == MusicScene.Results)
        {
            // cada entrada a resultados parte del principio, salvo que la melodía siga sonando (entrada y salida muy
            // rápidas): ahí continúa, sin saltos ni clics
            var results = _states[(int)MusicTrack.Results];
            if (results.Level <= Silent && !results.Channel.IsPlaying)
            {
                results.RestartPending = true;
            }
        }

        _scene = scene;
    }

    private float TargetOf(MusicTrack track)
    {
        if (!_states[(int)track].Channel.IsAvailable)
        {
            return 0f; // sin archivo: esa pista no suena (y el resto sigue funcionando)
        }

        return (_scene, track) switch
        {
            (MusicScene.Navigation, MusicTrack.Menu) => 1f,
            (MusicScene.Results, MusicTrack.Results) => 1f,
            _ => 0f,
        };
    }

    /// <summary>Pone la salida de cada pista en consonancia con su nivel: arranca, ajusta el volumen o se detiene.</summary>
    private void Drive()
    {
        foreach (var state in _states)
        {
            var gain = state.Level * _volume;
            if (gain > Silent)
            {
                if (!state.Channel.IsPlaying)
                {
                    if (_now < state.RetryAt)
                    {
                        continue;
                    }

                    if (!state.Channel.Start(state.RestartPending))
                    {
                        state.RetryAt = _now + RetryEvery;
                        continue;
                    }

                    state.RestartPending = false;
                }

                state.Channel.SetGain(gain);
            }
            else if (state.Channel.IsPlaying)
            {
                state.Channel.SetGain(0);
                state.Channel.Stop(); // silencio total: se libera el dispositivo; el loop conserva su posición
            }
        }
    }
}

/// <summary>El volumen de la música de interfaz como lo ve el usuario: pasos de 10 % de 0 a 100.</summary>
public static class MusicVolume
{
    public const int DefaultPercent = 20;

    public static IReadOnlyList<int> Steps { get; } = [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];

    public static int Clamp(int percent) => Math.Clamp(percent, 0, 100);

    /// <summary>Índice del paso más cercano a un porcentaje cualquiera.</summary>
    public static int IndexOf(int percent) =>
        Steps.Select((p, i) => (Distance: Math.Abs(p - Clamp(percent)), Index: i)).MinBy(x => x.Distance).Index;

    public static float ToGain(int percent) => Clamp(percent) / 100f;
}
