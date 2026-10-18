// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kantor.Core.Songs;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Kantor.App.Video;

/// <summary>
/// Video de fondo: libVLC decodifica (sin audio) a un búfer y se dibuja en un <see cref="WriteableBitmap"/>, así
/// la interfaz se superpone sin problemas de ventana. El audio de la canción manda: este componente solo sigue
/// su reloj (<see cref="Sync"/>) con <see cref="VideoSync"/>. Si libVLC no está disponible queda deshabilitado
/// (<see cref="Failure"/> dice por qué) y el escenario se ve como siempre.
/// </summary>
public sealed class VideoBackground : FrameworkElement, IDisposable
{
    private const uint MaxVideoWidth = 960;
    private static readonly TimeSpan SeekCooldown = TimeSpan.FromSeconds(1);

    // El motor libVLC es uno solo para toda la aplicación (fondo de la canción y vista previa de la lista): cada
    // instancia tiene su propio reproductor y el motor se libera con la última.
    private static LibVLC? s_vlc;
    private static int s_users;

    private MediaPlayer? _player;
    private Media? _media;
    private string? _path;

    // búfer de cuadros: VLC escribe en uno y la interfaz lee del otro
    private IntPtr[] _buffers = [IntPtr.Zero, IntPtr.Zero];
    private int _writing;
    private int _ready = -1;
    private uint _width;
    private uint _height;
    private uint _pitch;
    private WriteableBitmap? _bitmap;
    private readonly object _sync = new();
    private DateTime _lastSeek = DateTime.MinValue;
    private bool _wantPlaying;
    private bool _firstFrameRaised;

    // los delegados deben vivir mientras VLC los use
    private MediaPlayer.LibVLCVideoFormatCb? _formatCb;
    private MediaPlayer.LibVLCVideoCleanupCb? _cleanupCb;
    private MediaPlayer.LibVLCVideoLockCb? _lockCb;
    private MediaPlayer.LibVLCVideoUnlockCb? _unlockCb;
    private MediaPlayer.LibVLCVideoDisplayCb? _displayCb;

    private readonly DispatcherTimer _frameTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };

    /// <summary>Motivo por el que el video no está disponible, o <c>null</c> si funciona.</summary>
    public string? Failure { get; private set; }

    /// <summary>Hay un video cargado y mostrándose (o listo para mostrarse).</summary>
    public bool HasVideo => _path is not null && Failure is null;

    /// <summary>Ya llegó al menos un cuadro del video cargado.</summary>
    public bool HasFrame => _bitmap is not null;

    /// <summary>Llegó el primer cuadro del video cargado (para mostrar el video recién cuando hay algo que ver).</summary>
    public event EventHandler? FirstFrame;

    public VideoBackground()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _frameTimer.Tick += (_, _) => PullFrame();
    }

    /// <summary>Carga un video (o <c>null</c> para quitar el actual). No lo reproduce hasta <see cref="Sync"/>.</summary>
    public void Load(string? path)
    {
        Unload();
        if (path is null || !File.Exists(path))
        {
            return;
        }

        if (_player is null && s_warm is { IsCompleted: false })
        {
            // libVLC todavía se está preparando: no se bloquea la interfaz esperándolo. El video se abre cuando
            // esté listo (si mientras tanto se pidió otro o se descartó, no se hace nada).
            _path = path;
            Diag.Mark("video: libVLC todavía no está listo, se abrirá cuando termine");
            s_warm.ContinueWith(_ =>
            {
                if (_path == path && _media is null)
                {
                    Open(path);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        Open(path);
    }

    private void Open(string path)
    {
        if (!EnsureEngine())
        {
            _path = null;
            return;
        }

        try
        {
            _path = path;
            using (Diag.Time("libVLC: abrir medio", 50))
            {
                _media = new Media(s_vlc!, path, FromType.FromPath);
                _media.AddOption(":no-audio");
                _media.AddOption(":no-sub-autodetect-file");
                _player!.Media = _media;
            }

            _frameTimer.Start();
        }
        catch (Exception e) when (e is VLCException or InvalidOperationException or DllNotFoundException)
        {
            Failure = $"No se pudo cargar el video: {e.Message}";
            Unload();
        }
    }

    public void Unload()
    {
        _frameTimer.Stop();
        _wantPlaying = false;
        try
        {
            _player?.Stop();
        }
        catch (Exception e) when (e is VLCException or InvalidOperationException)
        {
            // al descartar no importa
        }

        _media?.Dispose();
        _media = null;
        _path = null;
        lock (_sync)
        {
            _ready = -1;
        }

        _bitmap = null;
        _firstFrameRaised = false;
        InvalidateVisual();
    }

    /// <summary>
    /// Sigue al reloj del audio. Se llama periódicamente (unas 30 veces por segundo): arranca, pausa o corrige
    /// el video según la posición de la canción y su #VIDEOGAP.
    /// </summary>
    public void Sync(TimeSpan songPosition, bool playing, double videoGapSeconds)
    {
        if (_player is null || _media is null)
        {
            return;
        }

        try
        {
            var target = VideoSync.Target(songPosition, videoGapSeconds);
            var started = VideoSync.HasStarted(target);

            if (!playing || !started)
            {
                if (_wantPlaying && _player.IsPlaying)
                {
                    _player.SetPause(true);
                }

                _wantPlaying = false;
                if (!playing && started && _player.State is VLCState.Paused or VLCState.Playing
                    && VideoSync.NeedsCorrection(TimeSpan.FromMilliseconds(Math.Max(0, _player.Time)), target)
                    && CanSeek())
                {
                    _player.Time = (long)target.TotalMilliseconds; // pausa/stop: el cuadro sigue a la posición
                }

                return;
            }

            if (!_wantPlaying)
            {
                _wantPlaying = true;
                if (_player.State is VLCState.Paused)
                {
                    _player.SetPause(false);
                }
                else if (!_player.IsPlaying)
                {
                    _player.Play();
                }

                MarkSeek();
                _player.Time = (long)target.TotalMilliseconds;
                return;
            }

            if (_player.IsPlaying
                && VideoSync.NeedsCorrection(TimeSpan.FromMilliseconds(Math.Max(0, _player.Time)), target)
                && CanSeek())
            {
                MarkSeek();
                _player.Time = (long)target.TotalMilliseconds;
            }
        }
        catch (Exception e) when (e is VLCException or InvalidOperationException)
        {
            Failure = $"El video falló: {e.Message}";
            Unload();
        }
    }

    private bool CanSeek() => DateTime.UtcNow - _lastSeek > SeekCooldown;

    private void MarkSeek() => _lastSeek = DateTime.UtcNow;

    // ---- libVLC -------------------------------------------------------------------------------------------

    private static Task<LibVLC>? s_warm;

    private static LibVLC CreateEngine()
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC("--no-audio", "--no-osd", "--no-video-title-show", "--quiet", "--no-stats");
    }

    /// <summary>Inicia en segundo plano la creación de libVLC, que es lo más lento de abrir el primer video.</summary>
    public void WarmUp() => s_warm ??= Task.Run(CreateEngine);

    private bool EnsureEngine()
    {
        if (_player is not null)
        {
            return true;
        }

        if (Failure is not null)
        {
            return false;
        }

        try
        {
            // Crear LibVLC (carga e indexa sus plugins) puede tardar muchos segundos la primera vez: por eso se
            // adelanta en segundo plano al abrir la aplicación (WarmUp) y acá solo se espera si todavía no terminó.
            using (Diag.Time("libVLC: motor", 50))
            {
                s_vlc ??= (s_warm ??= Task.Run(CreateEngine)).GetAwaiter().GetResult();
            }

            _player = new MediaPlayer(s_vlc);
            s_users++;
            _formatCb = OnFormat;
            _cleanupCb = OnCleanup;
            _lockCb = OnLock;
            _unlockCb = OnUnlock;
            _displayCb = OnDisplay;
            _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
            _player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);
            return true;
        }
        catch (Exception e) when (e is VLCException or DllNotFoundException or InvalidOperationException
                                      or BadImageFormatException or NullReferenceException)
        {
            Failure = $"libVLC no está disponible: {e.Message}";
            return false;
        }
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // BGRA de 32 bits; ancho acotado (el fondo no necesita más) y proporción conservada
        Marshal.Copy(System.Text.Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);
        if (width > MaxVideoWidth)
        {
            height = (uint)Math.Max(2, (long)height * MaxVideoWidth / width) & ~1u;
            width = MaxVideoWidth;
        }

        width &= ~1u;
        _width = width;
        _height = height;
        _pitch = width * 4;
        pitches = _pitch;
        lines = _height;

        lock (_sync)
        {
            FreeBuffers();
            var size = (int)(_pitch * _height);
            _buffers = [Marshal.AllocHGlobal(size), Marshal.AllocHGlobal(size)];
            _writing = 0;
            _ready = -1;
        }

        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_sync)
        {
            _ready = -1;
        }
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        lock (_sync)
        {
            var buffer = _buffers[_writing];
            Marshal.WriteIntPtr(planes, buffer);
            return IntPtr.Zero;
        }
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_sync)
        {
            _ready = _writing;
            _writing = 1 - _writing;
        }
    }

    private void PullFrame()
    {
        int index;
        uint width;
        uint height;
        uint pitch;
        IntPtr buffer;
        lock (_sync)
        {
            index = _ready;
            if (index < 0)
            {
                return;
            }

            _ready = -1;
            buffer = _buffers[index];
            (width, height, pitch) = (_width, _height, _pitch);
        }

        if (buffer == IntPtr.Zero || width == 0 || height == 0)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != (int)width || _bitmap.PixelHeight != (int)height)
        {
            _bitmap = new WriteableBitmap((int)width, (int)height, 96, 96, PixelFormats.Bgr32, null);
        }

        // se copia bajo el lock: VLC no escribe en este búfer hasta el próximo cuadro (doble búfer)
        _bitmap.WritePixels(new Int32Rect(0, 0, (int)width, (int)height), buffer, (int)(pitch * height), (int)pitch);
        InvalidateVisual();
        if (!_firstFrameRaised)
        {
            _firstFrameRaised = true;
            FirstFrame?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_bitmap is null || ActualWidth < 1 || ActualHeight < 1)
        {
            return;
        }

        // cubre todo el escenario conservando la proporción (recorta lo que sobra)
        var scale = Math.Max(ActualWidth / _bitmap.PixelWidth, ActualHeight / _bitmap.PixelHeight);
        var w = _bitmap.PixelWidth * scale;
        var h = _bitmap.PixelHeight * scale;
        dc.DrawImage(_bitmap, new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h));
    }

    private void FreeBuffers()
    {
        foreach (var buffer in _buffers)
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        _buffers = [IntPtr.Zero, IntPtr.Zero];
    }

    public void Dispose()
    {
        Unload();
        if (_player is not null)
        {
            _player.Dispose();
            _player = null;
            if (--s_users == 0)
            {
                s_vlc?.Dispose();
                s_vlc = null;
                s_warm = null;
            }
        }

        lock (_sync)
        {
            FreeBuffers();
        }
    }
}
