namespace Kantor.Core.Songs;

public static class SongMedia
{
    /// <summary>
    /// Resuelve un nombre de archivo del TXT dentro de la carpeta de la canción.
    /// Devuelve null si el nombre es vacío o intenta salir de la carpeta (por ejemplo "..\x.mp3").
    /// No comprueba que el archivo exista.
    /// </summary>
    public static string? ResolveInside(string directory, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName))
        {
            return null;
        }

        var root = Path.GetFullPath(directory);
        var full = Path.GetFullPath(Path.Combine(root, fileName));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
