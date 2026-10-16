// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Security.Cryptography;
using System.Text;
using Kantor.Core.Library;

namespace Kantor.Core.Data;

/// <summary>Un jugador del historial: perfil persistente. Archivarlo no borra nada de lo que jugó.</summary>
public sealed record PlayerRecord(
    long Id,
    string Name,
    string CharacterId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    bool Archived);

/// <summary>Una sesión de juego. Abierta mientras no tenga fecha de cierre; solo puede haber una abierta.</summary>
public sealed record SessionRecord(long Id, string? Name, DateTimeOffset StartedAt, DateTimeOffset? ClosedAt)
{
    public bool IsOpen => ClosedAt is null;
}

/// <summary>Quién ocupa un puesto (P1–P4) en la selección activa de una sesión.</summary>
public readonly record struct SelectionEntry(int Slot, long PlayerId);

/// <summary>
/// Un resultado a guardar: el jugador, el puesto que ocupó (P1–P4), su puntaje final y el comentario que se
/// mostró (<c>null</c> si no hubo; se guarda para no volver a sortearlo al consultar).
/// </summary>
public readonly record struct ResultInput(long PlayerId, int Slot, int Score, string? CommentId = null);

/// <summary>Una interpretación terminada, lista para guardarse junto con todos sus resultados.</summary>
/// <param name="Id">
/// Identificador de la ejecución (se genera al empezar a cantar y se reutiliza si el aviso de fin se repite):
/// guardar dos veces el mismo no duplica nada.
/// </param>
public sealed record NewPerformance(
    string Id,
    string SongId,
    string Title,
    string Artist,
    int Difficulty,
    int ScoringVersion,
    IReadOnlyList<ResultInput> Results,
    string? LibraryEntryId = null,
    string? Edition = null);

/// <param name="SongId">Identidad de la canción (artista + título); no distingue ediciones.</param>
/// <param name="LibraryEntryId">Entrada concreta de la biblioteca; <c>null</c> en lo guardado antes de la v3.</param>
/// <param name="Edition">Edición de la entrada al cantar; <c>null</c> si no tenía o es anterior a la v3.</param>
public sealed record PerformanceRecord(
    string Id,
    long SessionId,
    string SongId,
    string Title,
    string Artist,
    DateTimeOffset FinishedAt,
    int Difficulty,
    int ScoringVersion,
    string? LibraryEntryId = null,
    string? Edition = null);

/// <summary>
/// Un resultado con el nombre actual del jugador. <see cref="Slot"/> es el puesto de micrófono (P1–P4) y
/// <see cref="Position"/> la posición en la clasificación de esa interpretación, con empates compartidos.
/// <see cref="CommentId"/> es el comentario que se mostró (<c>null</c> en lo anterior a la v3).
/// </summary>
public sealed record PerformanceResultView(
    long PlayerId, string PlayerName, int Slot, int Score, int Position, string? CommentId = null);

public sealed record PerformanceView(PerformanceRecord Performance, IReadOnlyList<PerformanceResultView> Results);

/// <summary>Una fila del historial de un jugador.</summary>
public sealed record PlayerHistoryRow(
    PerformanceRecord Performance, long SessionId, int Slot, int Score, int Position);

/// <summary>Resultado de <see cref="KantorStore.SavePerformance"/>.</summary>
/// <param name="AlreadySaved">Esa ejecución ya estaba guardada: no se cambió nada.</param>
/// <param name="SessionCreated">No había sesión abierta y se creó una automáticamente.</param>
public sealed record SaveOutcome(long SessionId, bool AlreadySaved, bool SessionCreated);

/// <summary>Una regla del modelo no se cumple (jugador inexistente o archivado, puesto repetido, puntaje fuera de rango…).</summary>
public sealed class StoreRuleException(string message) : Exception(message);

/// <summary>Identificador estable de una canción, independiente de dónde esté el archivo.</summary>
public static class SongIdentity
{
    /// <summary>SHA-256 (hex) de artista y título normalizados (minúsculas, sin acentos).</summary>
    public static string For(string artist, string title)
    {
        var text = $"{SongSearch.Normalize(artist.Trim())}\n{SongSearch.Normalize(title.Trim())}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}

/// <summary>Posición en la clasificación a partir de los puntajes: los empates comparten posición (1, 1, 3).</summary>
public static class ScoreRanking
{
    public static int[] Positions(IReadOnlyList<int> scores)
    {
        var positions = new int[scores.Count];
        for (var i = 0; i < scores.Count; i++)
        {
            positions[i] = 1 + scores.Count(other => other > scores[i]);
        }

        return positions;
    }
}
