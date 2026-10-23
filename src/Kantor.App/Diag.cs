// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Kantor.Core.Settings;

namespace Kantor.App;

/// <summary>
/// Registro de diagnóstico (<c>kantor.log</c> junto a los ajustes). Anota marcas de tiempo de las etapas de carga y,
/// con un vigilante en un hilo aparte, cada vez que la interfaz tarda más de medio segundo en responder, con la
/// última etapa marcada: así un cuelgue deja escrito dónde ocurrió. Se reinicia al pasar de 512 KB.
/// </summary>
public static class Diag
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string? _path;
    private static volatile string _last = "inicio";

    public static void Start(Dispatcher ui)
    {
        try
        {
            _path = Path.Combine(AppPaths.DataDirectory(), "kantor.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > 512 * 1024)
            {
                File.Delete(_path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _path = null;
            return;
        }

        Mark("=== Kantaria inicia ===");
        var watcher = new Thread(() => Watch(ui)) { IsBackground = true, Name = "Kantor.Diag" };
        watcher.Start();
    }

    /// <summary>Anota una etapa (y la recuerda como "última etapa" para el vigilante).</summary>
    public static void Mark(string stage)
    {
        _last = stage;
        Write(stage);
    }

    /// <summary>Mide un bloque: anota su duración si pasa de <paramref name="thresholdMs"/>.</summary>
    public static IDisposable Time(string name, int thresholdMs = 100) => new Timer(name, thresholdMs);

    private static void Watch(Dispatcher ui)
    {
        while (true)
        {
            var sent = Clock.Elapsed;
            var done = new ManualResetEventSlim();
            ui.BeginInvoke(() => done.Set(), DispatcherPriority.Normal);
            if (!done.Wait(500))
            {
                var stage = _last;
                done.Wait();
                Write($"INTERFAZ COLGADA {(Clock.Elapsed - sent).TotalSeconds:0.0} s (última etapa: {stage})");
            }

            Thread.Sleep(250);
        }
    }

    private static void Write(string text)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,2}] {text}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // el diagnóstico nunca debe romper la aplicación
        }
    }

    private sealed class Timer(string name, int thresholdMs) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();

        public void Dispose()
        {
            var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            if (ms >= thresholdMs)
            {
                Write($"{name}: {ms:0} ms");
            }
        }
    }
}
