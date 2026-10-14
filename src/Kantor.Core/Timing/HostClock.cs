using System.Diagnostics;

namespace Kantor.Core.Timing;

/// <summary>
/// Base de tiempo común entre reproducción y captura: el contador de rendimiento de Windows (QPC)
/// expresado en unidades de 100 ns, que es la misma unidad en la que WASAPI informa las marcas de
/// tiempo de sus paquetes y de su reloj de posición.
/// </summary>
public static class HostClock
{
    public const long TicksPerSecond = 10_000_000;

    public static long Now() =>
        (long)(Stopwatch.GetTimestamp() * (TicksPerSecond / (double)Stopwatch.Frequency));

    public static double ToSeconds(long hostTicks) => hostTicks / (double)TicksPerSecond;

    public static long FromSeconds(double seconds) => (long)Math.Round(seconds * TicksPerSecond);
}

/// <summary>Convierte un instante de la base de tiempo común en posición dentro de la canción.</summary>
public interface ISongTimeMap
{
    /// <summary>
    /// Posición de la canción en el instante <paramref name="hostTicks"/>, o <c>null</c> si en ese
    /// momento la canción no está sonando (detenida o en pausa).
    /// </summary>
    TimeSpan? SongPositionAt(long hostTicks);
}
