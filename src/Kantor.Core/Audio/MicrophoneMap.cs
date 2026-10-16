// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Audio;

/// <summary>De dónde sale la voz de un micrófono: un canal de un dispositivo (o de un controlador ASIO).</summary>
public readonly record struct MicrophoneSource(string DeviceId, int Channel);

/// <summary>
/// Qué canal de qué dispositivo alimenta a cada puesto (0 a <see cref="MaxSlots"/> − 1). Un puesto es un
/// micrófono y, mientras no haya sesiones, un jugador anónimo ("Jugador 1" …). Un mismo canal solo puede
/// alimentar un puesto; los puestos pueden quedar vacíos en el medio. Seguro entre hilos: la captura consulta
/// el mapa mientras la interfaz lo modifica.
/// </summary>
public sealed class MicrophoneMap
{
    public const int MaxSlots = 4;

    private readonly object _gate = new();
    private readonly MicrophoneSource?[] _slots = new MicrophoneSource?[MaxSlots];

    /// <summary>La fuente del puesto, o <c>null</c> si está vacío.</summary>
    public MicrophoneSource? this[int slot]
    {
        get
        {
            lock (_gate)
            {
                return InRange(slot) ? _slots[slot] : null;
            }
        }
    }

    /// <summary>Puestos con micrófono, en orden.</summary>
    public IReadOnlyList<int> ActiveSlots
    {
        get
        {
            lock (_gate)
            {
                return Enumerable.Range(0, MaxSlots).Where(i => _slots[i] is not null).ToArray();
            }
        }
    }

    public int Count => ActiveSlots.Count;

    /// <summary>
    /// Asigna la fuente al puesto. Si otro puesto ya la usaba, se la quita (un canal no alimenta a dos
    /// jugadores). Devuelve el puesto que la perdió, o −1.
    /// </summary>
    public int Assign(int slot, MicrophoneSource source)
    {
        if (!InRange(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        lock (_gate)
        {
            var taken = FindUnlocked(source);
            if (taken == slot)
            {
                return -1;
            }

            if (taken >= 0)
            {
                _slots[taken] = null;
            }

            _slots[slot] = source;
            return taken;
        }
    }

    /// <summary>Vacía el puesto. Devuelve <c>false</c> si ya estaba vacío.</summary>
    public bool Unassign(int slot)
    {
        lock (_gate)
        {
            if (!InRange(slot) || _slots[slot] is null)
            {
                return false;
            }

            _slots[slot] = null;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_slots);
        }
    }

    /// <summary>Puesto que usa esa fuente, o −1.</summary>
    public int Find(string deviceId, int channel)
    {
        lock (_gate)
        {
            return FindUnlocked(new MicrophoneSource(deviceId, channel));
        }
    }

    /// <summary>Canales distintos de ese dispositivo que alimentan algún puesto.</summary>
    public IReadOnlyList<int> ChannelsOf(string deviceId)
    {
        lock (_gate)
        {
            return _slots.Where(s => s is { } v && v.DeviceId == deviceId).Select(s => s!.Value.Channel)
                .Distinct().ToArray();
        }
    }

    /// <summary>Dispositivos con al menos un puesto.</summary>
    public IReadOnlyList<string> DeviceIds()
    {
        lock (_gate)
        {
            return _slots.Where(s => s is not null).Select(s => s!.Value.DeviceId).Distinct().ToArray();
        }
    }

    private int FindUnlocked(MicrophoneSource source)
    {
        for (var i = 0; i < _slots.Length; i++)
        {
            if (_slots[i] is { } s && s.Channel == source.Channel && s.DeviceId == source.DeviceId)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool InRange(int slot) => slot >= 0 && slot < MaxSlots;
}

/// <summary>Qué voz de la canción canta cada puesto.</summary>
public static class VoiceAssignment
{
    /// <summary>
    /// Una canción de una voz la cantan todos; en un dueto el puesto 0 canta la voz 0, el 1 la voz 1 y los
    /// demás repiten en ese orden. Es el reparto por omisión: no hay elección por canción todavía.
    /// </summary>
    public static int VoiceFor(int slot, int voiceCount) => voiceCount <= 1 ? 0 : Math.Max(slot, 0) % voiceCount;
}
