// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kantor.Core.Characters;

/// <summary>
/// Ajustes opcionales del sprite de cuerpo entero de un personaje (sin interfaz: se definen junto a su
/// definición). Los valores predeterminados mantienen el comportamiento base del componente.
/// </summary>
/// <param name="VisualScale">Multiplicador del tamaño ajustado (1 = sin cambio); útil para personajes anchos.</param>
/// <param name="GroundOffset">Desplazamiento de la línea de pies (positivo = más abajo).</param>
/// <param name="Overflow">Cuánto puede sobresalir por arriba de su zona (fracción; 0,12 por defecto).</param>
public sealed record CharacterTuning(
    double VisualScale = 1.0,
    double GroundOffset = 0.0,
    double Overflow = Results.CharacterPlacement.DefaultOverflow);
