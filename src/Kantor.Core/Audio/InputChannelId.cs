// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Audio;

/// <summary>
/// Una entrada de audio concreta: un canal de un dispositivo. Un dispositivo no equivale a un micrófono:
/// una interfaz estéreo puede traer dos micrófonos independientes, uno por canal.
/// </summary>
/// <param name="DeviceId">Identificador estable del endpoint de Windows.</param>
/// <param name="Channel">Índice de canal, desde 0.</param>
public readonly record struct InputChannelId(string DeviceId, int Channel);
