// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kantor.App.Results;

namespace Kantor.App.Pixel;

/// <summary>
/// Selector visual de avatares: muestra la cara elegida y, al tocarla (clic, Enter o Espacio), pide abrir la grilla
/// de caras. La grilla no la dibuja este control: la pantalla que lo contiene la muestra dentro de su propia
/// composición (así nunca queda fuera de la aplicación, sea cual sea el tamaño de la ventana). Sin texto: los
/// avatares no tienen nombre.
/// </summary>
public sealed class FaceChoice : Button
{
    private const double ButtonFaceSize = 48;

    private IReadOnlyList<ImageSource?> _faces = [];
    private int _index = -1;

    public FaceChoice()
    {
        SetResourceReference(StyleProperty, "PixelTileButton");
        Padding = new Thickness(6);
        Render();
    }

    /// <summary>La cara elegida cambió por una acción del usuario (no al fijarla desde código).</summary>
    public event EventHandler? Changed;

    /// <summary>El usuario quiere elegir otra cara: la pantalla muestra la grilla y llama a <see cref="Pick"/>.</summary>
    public event EventHandler? OpenRequested;

    public int SelectedIndex => _index;

    public IReadOnlyList<ImageSource?> Faces => _faces;

    /// <summary>Define las caras y la elegida, sin avisar del cambio.</summary>
    public void Set(IReadOnlyList<ImageSource?> faces, int index)
    {
        _faces = faces;
        _index = faces.Count == 0 ? -1 : Math.Clamp(index, 0, faces.Count - 1);
        Render();
    }

    /// <summary>Elige una cara (acción del usuario): avisa solo si cambió.</summary>
    public void Pick(int index)
    {
        if (index < 0 || index >= _faces.Count || index == _index)
        {
            return;
        }

        _index = index;
        Render();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (_faces.Count > 1)
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Render() =>
        Content = PixelUi.Avatar(_index >= 0 ? _faces[_index] : null, PlayerColors.For(1), ButtonFaceSize);
}
