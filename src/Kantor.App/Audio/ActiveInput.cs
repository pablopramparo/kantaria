// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Audio;
using Kantor.Core.Pitch;
using Kantor.Core.Timing;

namespace Kantor.App.Audio;

/// <summary>
/// Las entradas de audio activas de la aplicación: hasta <see cref="MicrophoneMap.MaxSlots"/> micrófonos a la
/// vez, cada uno un canal de un dispositivo. Se abre una sola captura por dispositivo (varios micrófonos del
/// mismo dispositivo comparten la sesión) y cada análisis de tono se convierte a posición de canción en cuanto
/// llega, usando el instante de sus muestras, y se agrega a la línea temporal del puesto
/// (<see cref="TimelineOf"/>).
/// </summary>
public sealed class ActiveInput : IDisposable
{
    private sealed record Entry(AudioInputDevice Device, ICaptureSession Session,
        Action<int, PitchFrame> OnPitch, EventHandler<Exception?> OnStopped);

    private readonly ISongTimeMap _songTime;
    private readonly Func<AudioInputDevice, ICaptureSession> _createCapture;
    private readonly PitchLog? _log = PitchLog.FromEnvironment();
    private readonly Dictionary<string, Entry> _sessions = [];
    private readonly PitchTimeline[] _timelines =
        Enumerable.Range(0, MicrophoneMap.MaxSlots).Select(_ => new PitchTimeline()).ToArray();

    /// <param name="songTime">Cómo se ubica un instante de la captura en la canción (la reproducción activa).</param>
    /// <param name="createCapture">Abre la captura de un dispositivo con el motor de audio vigente.</param>
    public ActiveInput(ISongTimeMap songTime, Func<AudioInputDevice, ICaptureSession> createCapture)
    {
        _songTime = songTime;
        _createCapture = createCapture;
    }

    public MicrophoneMap Map { get; } = new();

    /// <summary>Se produce al abrir, cambiar o cerrar cualquier micrófono.</summary>
    public event EventHandler? Changed;

    /// <summary>Se produce si la captura de un dispositivo se detiene por un fallo; el argumento es la causa.</summary>
    public event EventHandler<Exception?>? Failed;

    /// <summary>Tonos detectados del micrófono de ese puesto, ya ubicados en la canción.</summary>
    public PitchTimeline TimelineOf(int slot) => _timelines[slot];

    /// <summary>Sesión que captura el micrófono del puesto, o <c>null</c> si no hay.</summary>
    public ICaptureSession? SessionOf(int slot) =>
        Map[slot] is { } source && _sessions.TryGetValue(source.DeviceId, out var entry) ? entry.Session : null;

    public AudioInputDevice? DeviceOf(int slot) =>
        Map[slot] is { } source && _sessions.TryGetValue(source.DeviceId, out var entry) ? entry.Device : null;

    /// <summary>Sesión abierta de un dispositivo, o <c>null</c> si no tiene micrófonos.</summary>
    public ICaptureSession? SessionFor(string deviceId) =>
        _sessions.TryGetValue(deviceId, out var entry) ? entry.Session : null;

    /// <summary>
    /// Pone el canal de ese dispositivo como micrófono del puesto. Si otro puesto ya usaba ese canal, lo pierde.
    /// Abre la captura del dispositivo si todavía no está abierta.
    /// </summary>
    public void Assign(int slot, AudioInputDevice device, int channel)
    {
        var previous = Map[slot];
        var lostSlot = Map.Assign(slot, new MicrophoneSource(device.Id, channel));
        try
        {
            EnsureSession(device);
            ApplyPitchChannels(device.Id);
        }
        catch
        {
            // No se pudo abrir: el puesto queda como estaba.
            if (previous is { } old)
            {
                Map.Assign(slot, old);
            }
            else
            {
                Map.Unassign(slot);
            }

            if (lostSlot >= 0)
            {
                Map.Assign(lostSlot, new MicrophoneSource(device.Id, channel));
            }

            throw;
        }

        _timelines[slot].Clear();
        if (lostSlot >= 0)
        {
            _timelines[lostSlot].Clear();
        }

        if (previous is { } was && was.DeviceId != device.Id)
        {
            ApplyPitchChannels(was.DeviceId); // el dispositivo anterior puede haberse quedado sin canales
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Quita el micrófono del puesto; si era el último canal de su dispositivo, cierra la captura.</summary>
    public void Unassign(int slot)
    {
        var source = Map[slot];
        if (source is null || !Map.Unassign(slot))
        {
            return;
        }

        _timelines[slot].Clear();
        ApplyPitchChannels(source.Value.DeviceId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Quita todos los micrófonos y cierra las capturas.</summary>
    public void Close() => CloseAll(notify: true);

    private void CloseAll(bool notify)
    {
        if (Map.Count == 0 && _sessions.Count == 0)
        {
            return;
        }

        Map.Clear();
        foreach (var timeline in _timelines)
        {
            timeline.Clear();
        }

        foreach (var id in _sessions.Keys.ToList())
        {
            CloseSession(id);
        }

        if (notify)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        CloseAll(notify: false);
        _log?.Dispose();
    }

    private void EnsureSession(AudioInputDevice device)
    {
        if (_sessions.ContainsKey(device.Id))
        {
            return;
        }

        var session = _createCapture(device);
        var id = device.Id;
        Action<int, PitchFrame> onPitch = (channel, frame) => OnPitchFrame(id, channel, frame);
        EventHandler<Exception?> onStopped = (_, error) => Failed?.Invoke(this, error);
        session.PitchFrameReady += onPitch;
        session.Stopped += onStopped;
        try
        {
            session.SetPitchChannels(Map.ChannelsOf(id));
            session.Start();
        }
        catch
        {
            session.PitchFrameReady -= onPitch;
            session.Stopped -= onStopped;
            session.Dispose();
            throw;
        }

        _sessions[id] = new Entry(device, session, onPitch, onStopped);
    }

    /// <summary>Analiza para tono solo los canales con un puesto; sin ninguno, la captura se cierra.</summary>
    private void ApplyPitchChannels(string deviceId)
    {
        if (!_sessions.TryGetValue(deviceId, out var entry))
        {
            return;
        }

        var channels = Map.ChannelsOf(deviceId);
        if (channels.Count == 0)
        {
            CloseSession(deviceId);
            return;
        }

        entry.Session.SetPitchChannels(channels);
    }

    private void CloseSession(string deviceId)
    {
        if (_sessions.Remove(deviceId, out var entry))
        {
            entry.Session.PitchFrameReady -= entry.OnPitch;
            entry.Session.Stopped -= entry.OnStopped;
            entry.Session.Dispose();
        }
    }

    private void OnPitchFrame(string deviceId, int channel, PitchFrame frame)
    {
        var slot = Map.Find(deviceId, channel);
        if (slot < 0)
        {
            return;
        }

        // Se ubica en la canción el centro de las muestras analizadas, no el momento de este cálculo.
        var located = _songTime.SongPositionAt(frame.CenterHost);
        _log?.Frame(channel, frame, located, SessionFor(deviceId)?.Discontinuities ?? 0, slot);
        if (located is not { } time)
        {
            return;
        }

        var window = TimeSpan.FromSeconds(HostClock.ToSeconds(frame.WindowEndHost - frame.WindowStartHost));
        _timelines[slot].Add(new PitchSample(time, window, frame.Frequency, frame.Confidence, frame.LevelDb));
    }
}
