using Kantor.Core.Pitch;
using Kantor.Core.Timing;

namespace Kantor.App.Audio;

/// <summary>
/// La entrada de audio activa de la aplicación: un dispositivo capturándose y el canal cuyo tono se
/// ubica en la canción. Cada análisis se convierte a posición de canción en cuanto llega, usando el
/// instante de sus muestras, y se agrega a <see cref="Timeline"/>.
/// </summary>
public sealed class ActiveInput : IDisposable
{
    private readonly ISongTimeMap _songTime;
    private CaptureSession? _session;
    private volatile int _channel;

    public ActiveInput(ISongTimeMap songTime)
    {
        _songTime = songTime;
    }

    public PitchTimeline Timeline { get; } = new();

    public CaptureSession? Session => _session;

    public int Channel => _channel;

    /// <summary>Se produce al abrir, cambiar o cerrar la entrada.</summary>
    public event EventHandler? Changed;

    /// <summary>Se produce si la captura se detiene por un fallo; el argumento es la causa.</summary>
    public event EventHandler<Exception?>? Failed;

    public void Open(AudioInputDevice device, int channel = 0)
    {
        Close();
        var session = new CaptureSession(device);
        session.SetPitchChannels([channel]);
        session.PitchFrameReady += OnPitchFrame;
        session.Stopped += OnStopped;
        _channel = channel;
        session.Start();
        _session = session;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SelectChannel(int channel)
    {
        if (_session is null || channel == _channel)
        {
            return;
        }

        _channel = channel;
        _session.SetPitchChannels([channel]);
        Timeline.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            session.PitchFrameReady -= OnPitchFrame;
            session.Stopped -= OnStopped;
            session.Dispose();
            Timeline.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose() => Close();

    private void OnPitchFrame(int channel, PitchFrame frame)
    {
        if (channel != _channel)
        {
            return;
        }

        // Se ubica en la canción el centro de las muestras analizadas, no el momento de este cálculo.
        if (_songTime.SongPositionAt(frame.CenterHost) is not { } time)
        {
            return;
        }

        var window = TimeSpan.FromSeconds(HostClock.ToSeconds(frame.WindowEndHost - frame.WindowStartHost));
        Timeline.Add(new PitchSample(time, window, frame.Frequency, frame.Confidence, frame.LevelDb));
    }

    private void OnStopped(object? sender, Exception? error) => Failed?.Invoke(this, error);
}
