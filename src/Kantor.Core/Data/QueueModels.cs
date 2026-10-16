// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
namespace Kantor.Core.Data;

/// <summary>
/// Estado de un elemento de la cola. <see cref="Removed"/> es interno: el elemento quitado se conserva para poder
/// distinguir "ya no está" de "nunca existió", pero no se muestra.
/// </summary>
public enum QueueState
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Cancelled = 3,
    Removed = 4,
}

/// <summary>A quién se asignó un puesto (P1–P4) en un elemento de la cola.</summary>
public readonly record struct QueueSlot(int Slot, long PlayerId);

/// <summary>
/// Un elemento de la cola. Guarda una instantánea de artista, título y edición: se puede mostrar aunque la entrada
/// de la biblioteca ya no esté.
/// </summary>
public sealed record QueueEntry(
    string ItemId,
    string LibraryEntryId,
    string SongIdentity,
    string Artist,
    string Title,
    string? Edition,
    QueueState State,
    int Order,
    IReadOnlyList<QueueSlot> Slots,
    string? AddedById,
    string? AddedByName,
    DateTimeOffset AddedAt,
    DateTimeOffset? FinishedAt,
    string? PerformanceId);

public enum QueueError
{
    /// <summary>Ese identificador no existe en esta cola.</summary>
    Unknown,

    /// <summary>Existió y ya no está pendiente (quitado, completado o cancelado).</summary>
    Gone,
    Full,
    Running,

    /// <summary>La revisión esperada ya no es la vigente.</summary>
    Conflict,
}

/// <summary>Una operación de la cola no se pudo aplicar; no cambió nada.</summary>
public sealed class QueueException(QueueError error, string message, string field = "itemId") : Exception(message)
{
    public QueueError Error { get; } = error;

    /// <summary>Qué argumento causó el error (<c>itemId</c> o <c>beforeItemId</c>).</summary>
    public string Field { get; } = field;
}

/// <summary>Un archivo de la biblioteca tal como lo vio el escaneo, para asignarle su identificador de entrada.</summary>
public readonly record struct LibraryFile(string Path, long ModifiedTicks, long Length);

/// <summary>Una entrada de la biblioteca persistida: identificador estable y último lugar donde se vio.</summary>
public sealed record LibraryEntryRow(string EntryId, string Path, string? ContentHash);

/// <summary>Clave de comparación de rutas: absoluta y sin distinguir mayúsculas.</summary>
public static class LibraryEntryKeys
{
    public static string KeyOf(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path).ToLowerInvariant();
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return path.ToLowerInvariant();
        }
    }

    /// <summary>SHA-256 (hex) del contenido del archivo, o <c>null</c> si no se puede leer.</summary>
    public static string? HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
