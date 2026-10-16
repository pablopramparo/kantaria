// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Globalization;
using System.IO;
using System.Text;
using Kantor.Core.Pitch;
using Kantor.Core.Timing;

namespace Kantor.App.Audio;

/// <summary>
/// Diagnóstico opcional: si la variable de entorno <c>KANTOR_PITCH_LOG</c> apunta a un archivo, cada análisis
/// de tono se agrega como una línea CSV. No cambia nada del comportamiento; sirve para ver por qué un tono
/// se corta (nivel bajo, señal poco periódica, cuadro descartado por falta de reloj, huecos del flujo).
/// </summary>
public sealed class PitchLog : IDisposable
{
    public const string Variable = "KANTOR_PITCH_LOG";

    private const string Header =
        "host_s,song_s,channel,level_db,hz,confidence,min_cmnd,voiced,dropped,discontinuities,slot";

    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private int _sinceFlush;

    private PitchLog(StreamWriter writer)
    {
        _writer = writer;
    }

    public static PitchLog? FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
            var writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            if (isNew)
            {
                writer.WriteLine(Header);
            }

            return new PitchLog(writer);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <param name="songTime">Posición en la canción del centro de la ventana; null si el cuadro se descartó.</param>
    public void Frame(int channel, PitchFrame frame, TimeSpan? songTime, int discontinuities, int slot = 0)
    {
        var inv = CultureInfo.InvariantCulture;
        var line = string.Create(inv,
            $"{HostClock.ToSeconds(frame.CenterHost):F4},{(songTime is { } t ? t.TotalSeconds.ToString("F4", inv) : string.Empty)}," +
            $"{channel},{frame.LevelDb:F1},{frame.Frequency:F1},{frame.Confidence:F3},{frame.MinCmnd:F3}," +
            $"{(frame.IsVoiced ? 1 : 0)},{(songTime is null ? 1 : 0)},{discontinuities},{slot}");

        lock (_gate)
        {
            _writer.WriteLine(line);
            if (++_sinceFlush >= 50)
            {
                _writer.Flush();
                _sinceFlush = 0;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }
}
