// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kantor.Core.Results;

public enum ResultsKey { Other, Enter, Space, Escape, Left, Right, End }

public enum ResultsKeyAction
{
    /// <summary>La pulsación no es de esta pantalla: sigue su curso normal.</summary>
    None,

    /// <summary>Completar la animación ya; la pulsación no debe activar nada.</summary>
    Skip,

    /// <summary>Tragarse la pulsación (repetición o liberación de la que saltó la animación).</summary>
    Swallow,

    /// <summary>Dejar que el botón con foco ejecute su acción.</summary>
    Activate,

    /// <summary>Cerrar la pantalla de resultados.</summary>
    Close,

    /// <summary>Pasar ya al siguiente participante de la ceremonia.</summary>
    Advance,

    /// <summary>Saltear la ceremonia y dejar la pantalla en su estado final.</summary>
    SkipCeremony,
}

/// <summary>
/// Reglas de teclado de la pantalla de resultados: Enter o Espacio durante la animación la completan sin activar
/// ningún botón (ni con la repetición de tecla ni al soltar); una pulsación posterior sí activa el botón.
/// Escape cierra siempre. Sin dependencias de WPF para poder probarse.
/// </summary>
public sealed class ResultsInputGate
{
    private ResultsKey? _swallowed;

    public ResultsKeyAction KeyDown(ResultsKey key, bool isAnimating, bool ceremonyActive = false)
    {
        if (key == ResultsKey.Escape)
        {
            _swallowed = null;
            return ResultsKeyAction.Close;
        }

        if (key == ResultsKey.End)
        {
            return ceremonyActive ? ResultsKeyAction.SkipCeremony : ResultsKeyAction.None;
        }

        if (key is not (ResultsKey.Enter or ResultsKey.Space))
        {
            return ResultsKeyAction.None;
        }

        if (_swallowed == key)
        {
            return ResultsKeyAction.Swallow; // repetición de la tecla que ya saltó la animación
        }

        if (isAnimating)
        {
            _swallowed = key;
            return ResultsKeyAction.Skip;
        }

        if (ceremonyActive)
        {
            _swallowed = key; // la repetición y la liberación de esta pulsación no activan ningún botón
            return ResultsKeyAction.Advance;
        }

        return ResultsKeyAction.Activate;
    }

    /// <summary>Devuelve <c>true</c> si la liberación pertenece a la pulsación que saltó la animación.</summary>
    public bool KeyUp(ResultsKey key)
    {
        if (_swallowed != key)
        {
            return false;
        }

        _swallowed = null;
        return true;
    }

    public void Reset() => _swallowed = null;
}
