// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Remote;

/// <summary>Lo que la máquina del avance automático necesita saber en cada revisión.</summary>
/// <param name="OnResults">Los resultados de una canción están en pantalla.</param>
/// <param name="ResultSeq">Identifica el resultado mostrado (cambia con cada canción terminada).</param>
/// <param name="Animating">La animación de entrada de los resultados sigue en curso.</param>
/// <param name="InteractionSeq">Crece con cada tecla o clic local posterior a la animación.</param>
/// <param name="Enabled">El ajuste está activo.</param>
/// <param name="BlockReason">Por qué no se puede avanzar (<c>null</c> = hay siguiente válida y cantantes válidos).</param>
public readonly record struct AutoAdvanceInput(
    bool OnResults, long ResultSeq, bool Animating, long InteractionSeq, bool Enabled, string? BlockReason);

public enum AutoAdvanceKind
{
    /// <summary>Sin cuenta.</summary>
    Idle,

    /// <summary>Contando: <see cref="AutoAdvanceOutput.Remaining"/> segundos.</summary>
    Counting,

    /// <summary>La cuenta llegó a cero: hay que iniciar la siguiente canción.</summary>
    Fire,

    /// <summary>Había una cuenta (o iba a haberla) y no se puede avanzar: <see cref="AutoAdvanceOutput.Reason"/>.</summary>
    Blocked,
}

public readonly record struct AutoAdvanceOutput(AutoAdvanceKind Kind, TimeSpan Remaining = default, string? Reason = null);

/// <summary>
/// Reglas del avance automático de la cola, sin reloj ni interfaz (se alimenta con instantes monotónicos).
/// <list type="bullet">
/// <item>Se arma una sola vez por resultado, cuando la animación terminó (o se salteó): si el ajuste estaba apagado
/// en ese momento no se arma, y activarlo después con los mismos resultados en pantalla tampoco los arma.</item>
/// <item>La cuenta dura <see cref="Protocol.AutoAdvanceDelay"/>.</item>
/// <item>La cancelan: apagar el ajuste, cualquier interacción local en resultados, salir de resultados y las
/// cancelaciones explícitas (<see cref="Cancel"/>). Una cancelada no se vuelve a armar para esos resultados.</item>
/// <item>Si deja de poder avanzar mientras cuenta (cola vacía, jugador archivado…) se cancela y se informa.</item>
/// </list>
/// </summary>
public sealed class AutoAdvanceMachine(TimeSpan? delay = null)
{
    private readonly TimeSpan _delay = delay ?? Protocol.AutoAdvanceDelay;
    private long _seq = -1;
    private long _baseInteraction;
    private bool _handled;
    private bool _cancelled;
    private TimeSpan? _deadline;

    /// <summary>Hay una cuenta en marcha.</summary>
    public bool Armed => _deadline is not null && !_cancelled;

    /// <summary>Cancela la cuenta actual y no la vuelve a armar para estos resultados.</summary>
    public void Cancel()
    {
        _deadline = null;
        _cancelled = true;
        _handled = true;
    }

    public AutoAdvanceOutput Step(TimeSpan now, AutoAdvanceInput input)
    {
        if (!input.OnResults)
        {
            _deadline = null;
            _seq = -1;
            _handled = false;
            _cancelled = false;
            return new AutoAdvanceOutput(AutoAdvanceKind.Idle);
        }

        if (input.ResultSeq != _seq)
        {
            _seq = input.ResultSeq;
            _baseInteraction = input.InteractionSeq;
            _handled = false;
            _cancelled = false;
            _deadline = null;
        }

        if (_cancelled)
        {
            return new AutoAdvanceOutput(AutoAdvanceKind.Idle);
        }

        if (_deadline is { } deadline)
        {
            if (!input.Enabled || input.InteractionSeq != _baseInteraction)
            {
                Cancel();
                return new AutoAdvanceOutput(AutoAdvanceKind.Idle);
            }

            if (input.BlockReason is { } blocked)
            {
                Cancel();
                return new AutoAdvanceOutput(AutoAdvanceKind.Blocked, Reason: blocked);
            }

            if (now >= deadline)
            {
                _deadline = null;
                _handled = true;
                _cancelled = true;
                return new AutoAdvanceOutput(AutoAdvanceKind.Fire);
            }

            return new AutoAdvanceOutput(AutoAdvanceKind.Counting, deadline - now);
        }

        if (_handled || input.Animating)
        {
            return new AutoAdvanceOutput(AutoAdvanceKind.Idle);
        }

        // La animación terminó (o se salteó): es el único momento en que se decide si se arma.
        _handled = true;
        _baseInteraction = input.InteractionSeq;
        if (!input.Enabled)
        {
            return new AutoAdvanceOutput(AutoAdvanceKind.Idle);
        }

        if (input.BlockReason is { } reason)
        {
            return new AutoAdvanceOutput(AutoAdvanceKind.Blocked, Reason: reason);
        }

        _deadline = now + _delay;
        return new AutoAdvanceOutput(AutoAdvanceKind.Counting, _delay);
    }
}
