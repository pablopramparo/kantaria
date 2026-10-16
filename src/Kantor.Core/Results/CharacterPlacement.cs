// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kantor.Core.Results;

/// <summary>Rectángulo en coordenadas de la zona del personaje (origen arriba a la izquierda).</summary>
public readonly record struct PlacementRect(double X, double Y, double Width, double Height);

/// <summary>
/// Dónde se dibuja el sprite de un personaje dentro de su zona. Sin dependencias de WPF.
/// Con los valores predeterminados (<c>visualScale</c> 1, <c>groundOffset</c> 0, <c>overflow</c> 0,12) el
/// resultado es el comportamiento base: el sprite se ajusta (sin deformarse) a la caja disponible, anclado por
/// los pies al centro, pudiendo sobresalir por arriba hasta <c>overflow</c> y un 7 % por cada costado.
/// </summary>
public static class CharacterPlacement
{
    public const double DefaultOverflow = 0.12;

    /// <summary>Margen inferior donde vive la sombra pixelada (unidades de diseño).</summary>
    public const double ShadowBand = 8;

    /// <param name="imageWidth">Ancho del PNG (cualquier proporción).</param>
    /// <param name="imageHeight">Alto del PNG.</param>
    /// <param name="zoneWidth">Ancho de la zona reservada.</param>
    /// <param name="zoneHeight">Alto de la zona reservada.</param>
    /// <param name="visualScale">
    /// Multiplicador del tamaño ajustado (1 = sin cambio). Sirve para agrandar personajes anchos, que de otro modo
    /// quedan chicos al ajustarse al ancho. El alto sigue acotado por <paramref name="overflow"/>.
    /// </param>
    /// <param name="groundOffset">
    /// Desplazamiento vertical de la línea de pies (positivo = más abajo), para sprites con margen transparente
    /// inferior o que "flotan".
    /// </param>
    /// <param name="overflow">Cuánto puede sobresalir por arriba de la zona (fracción de la altura).</param>
    public static PlacementRect Compute(
        double imageWidth, double imageHeight, double zoneWidth, double zoneHeight,
        double visualScale = 1.0, double groundOffset = 0.0, double overflow = DefaultOverflow)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || zoneWidth <= 0 || zoneHeight <= 0)
        {
            return new PlacementRect(0, 0, 0, 0);
        }

        var feetY = zoneHeight - ShadowBand + groundOffset;
        var boxW = zoneWidth * 1.15;
        var boxH = Math.Max(1, feetY * (1 + Math.Max(0, overflow)));
        var scale = Math.Min(boxW / imageWidth, boxH / imageHeight) * Math.Max(0.01, visualScale);
        if (imageHeight * scale > boxH)
        {
            scale = boxH / imageHeight; // el desborde superior es un límite configurable, no se supera
        }

        var w = imageWidth * scale;
        var h = imageHeight * scale;
        return new PlacementRect(zoneWidth / 2 - w / 2, feetY - h, w, h);
    }
}
