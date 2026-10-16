// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kantor.Core.Songs;

/// <summary>
/// Reglas de sincronización del video de fondo con el audio de la canción (sin dependencias de WPF ni de VLC).
/// El video se muestra en <c>posición del audio + #VIDEOGAP</c>; el reloj de la canción es la única referencia.
/// </summary>
public static class VideoSync
{
    /// <summary>Diferencia tolerada antes de corregir saltando el video (evita tirones por jitter).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(350);

    /// <summary>Posición del video que corresponde a esa posición del audio. Puede ser negativa (aún no empieza).</summary>
    public static TimeSpan Target(TimeSpan songPosition, double videoGapSeconds) =>
        songPosition + TimeSpan.FromSeconds(videoGapSeconds);

    /// <summary>El video ya debería estar mostrándose (su posición objetivo es válida).</summary>
    public static bool HasStarted(TimeSpan target) => target >= TimeSpan.Zero;

    /// <summary>Hay que saltar el video a la posición objetivo porque se desfasó más de la tolerancia.</summary>
    public static bool NeedsCorrection(TimeSpan videoPosition, TimeSpan target) =>
        (videoPosition - target).Duration() > Tolerance;
}
