// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Globalization;
using System.Text;

namespace Kantor.Core.Data;

/// <summary>Exporta el historial a CSV (UTF-8, coma, comillas dobles; fechas ISO 8601): una fila por resultado.</summary>
public static class HistoryExport
{
    public const string Header =
        "partida,inicio_partida,fecha,artista,titulo,dificultad,jugador,micrófono,puntaje,posicion,puntos_competicion";

    public static string ToCsv(IReadOnlyList<PerformanceView> performances, IReadOnlyList<SessionRecord> sessions)
    {
        var bySession = sessions.ToDictionary(s => s.Id);
        var text = new StringBuilder(Header).Append("\r\n");
        foreach (var view in performances)
        {
            var p = view.Performance;
            bySession.TryGetValue(p.SessionId, out var session);
            var points = RankingStats.CompetitionPoints(view.Results);
            for (var i = 0; i < view.Results.Count; i++)
            {
                var r = view.Results[i];
                text.Append(string.Join(',',
                    Field(session is null ? p.SessionId.ToString(CultureInfo.InvariantCulture)
                        : string.IsNullOrWhiteSpace(session.Name) ? "#" + session.Id : session.Name),
                    Field(session?.StartedAt.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                    Field(p.FinishedAt.ToString("O", CultureInfo.InvariantCulture)),
                    Field(p.Artist),
                    Field(p.Title),
                    Field(p.Difficulty switch { 1 => "medio", 2 => "dificil", _ => "facil" }),
                    Field(r.PlayerName),
                    r.Slot.ToString(CultureInfo.InvariantCulture),
                    r.Score.ToString(CultureInfo.InvariantCulture),
                    r.Position.ToString(CultureInfo.InvariantCulture),
                    points[i].ToString(CultureInfo.InvariantCulture))).Append("\r\n");
            }
        }

        return text.ToString();
    }

    private static string Field(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
