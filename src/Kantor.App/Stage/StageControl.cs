// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Kantor.App.Pixel;
using Kantor.Core.Feedback;
using Kantor.Core.Pitch;
using Kantor.Core.Scoring;
using Kantor.Core.Songs;
using Kantor.Core.Timing;

namespace Kantor.App.Stage;

/// <summary>Cómo se muestra la voz del jugador sobre las notas.</summary>
public enum VoiceDisplay
{
    /// <summary>Por beat, como UltraStar: barras sobre la nota si acierta y al tono real si no.</summary>
    Beats,

    /// <summary>Cada análisis del detector tal como llega: útil para ver qué detecta el micrófono.</summary>
    Raw,
}

/// <summary>
/// Un jugador en el escenario: de qué micrófono sale su voz, cómo se puntúa y con qué color se muestra. Con
/// varios jugadores en la misma voz, sus barras comparten la nota en franjas, una por jugador.
/// </summary>
public sealed class StagePlayer
{
    /// <summary>Número de jugador (1 a 4); define el color y el orden de las franjas.</summary>
    public int Number { get; init; } = 1;

    public string Name { get; init; } = "JUGADOR 1";

    public Color Color { get; init; } = Color.FromRgb(0x19, 0xD3, 0xFF);

    /// <summary>Evaluación por beat de su voz; sin ella el modo por beat no dibuja nada.</summary>
    public VoiceScorer? Scorer { get; init; }

    /// <summary>Tonos detectados en su micrófono; <c>null</c> si no tiene.</summary>
    public PitchTimeline? Timeline { get; init; }

    /// <summary>Índice de la voz de la canción que canta.</summary>
    public int VoiceIndex { get; init; }

    /// <summary>
    /// Retardo con el que llega su voz respecto de la canción (el mismo que usa su puntaje). La estela cruda se
    /// dibuja ese tiempo antes para que quede alineada con la nota que se estaba cantando.
    /// </summary>
    public TimeSpan Delay { get; init; }

    /// <summary>Retrato para el HUD; <c>null</c> dibuja una silueta del color del jugador.</summary>
    public ImageSource? Portrait { get; init; }
}

/// <summary>
/// Dibuja el escenario de la canción (notas, voz, letra y HUD) según la posición de un <see cref="ISongClock"/>.
/// No conoce el reproductor de audio: solo consulta la posición. Estilo arcade: superficies chicas y oscuras con
/// marcos escalonados, contornos y sombras duras, para leerse sobre cualquier video de fondo.
/// </summary>
public sealed class StageControl : FrameworkElement
{
    private const double SecondsBehind = 2.0;
    private const double SecondsAhead = 6.0;
    private const double NowLineFraction = 0.22;

    // UltraStar dibuja un fallo al 65 % de la altura de un acierto.
    private const double MissHeightFraction = 0.65;

    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x30)));
    private static readonly Brush Strip = Freeze(new SolidColorBrush(Color.FromArgb(0x4C, 0x07, 0x09, 0x1C)));
    private static readonly Brush LyricsBand = Freeze(new SolidColorBrush(Color.FromArgb(0xC4, 0x07, 0x09, 0x1C)));
    private static readonly Brush StripBorder = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x33, 0x70)));
    private static readonly Brush Hud = Freeze(new SolidColorBrush(Color.FromArgb(0xD0, 0x07, 0x09, 0x1C)));
    private static readonly Brush Ink = Freeze(new SolidColorBrush(Color.FromRgb(0x05, 0x07, 0x18)));
    private static readonly Brush ShadowBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0x00, 0x08)));
    private static readonly Pen Grid = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0xB8, 0xC4, 0xFF)), 1));

    private static readonly Color PlayerColor = Color.FromRgb(0x19, 0xD3, 0xFF);
    private static readonly Brush Player = Freeze(new SolidColorBrush(PlayerColor));
    private static readonly Brush Gold = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC9, 0x33)));
    private static readonly Brush RapBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x2A)));
    private static readonly Brush White = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xFF)));
    private static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromRgb(0xA9, 0xB1, 0xDA)));

    private static readonly Brush SlotFill = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x1B, 0x4A)));
    private static readonly Brush SlotGoldFill = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x35, 0x08)));
    private static readonly Brush SlotRapFill = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x24, 0x08)));
    private static readonly Brush ActiveFill = Freeze(new SolidColorBrush(Color.FromRgb(0x5A, 0x66, 0xB8)));

    private static readonly Dictionary<(Color, byte), Brush> PlayerBrushes = [];

    /// <summary>Pincel congelado del color del jugador con esa opacidad (en caché: se pide cada cuadro).</summary>
    private static Brush BrushOf(Color color, byte alpha = 0xFF)
    {
        if (!PlayerBrushes.TryGetValue((color, alpha), out var brush))
        {
            brush = Freeze(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)));
            PlayerBrushes[(color, alpha)] = brush;
        }

        return brush;
    }

    private static readonly Typeface Body = new("Segoe UI Semibold");
    private static readonly Typeface BodyPlain = new("Segoe UI");
    private static readonly Typeface PixelFace = ResolvePixelFace();

    private readonly List<PitchSample> _pitchBuffer = [];
    private readonly List<BeatResult> _barSource = [];

    private Song? _song;
    private ISongClock? _clock;
    private SongTiming _timing;

    /// <summary>Cómo se dibuja la voz de los jugadores; se puede cambiar en cualquier momento.</summary>
    public VoiceDisplay VoiceMode { get; set; } = VoiceDisplay.Beats;

    /// <summary>Jugadores en pantalla (HUD y voz sobre las notas), en orden de número.</summary>
    public IReadOnlyList<StagePlayer> Players { get; set; } = [];

    /// <summary>Con un video de fondo, el escenario deja de pintar su fondo para dejarlo ver.</summary>
    public bool TranslucentBackground { get; set; }

    /// <summary>Duración total de la canción (para la barra de progreso). Cero = sin barra.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Texto de la etiqueta de precisión del HUD.</summary>
    public string AccuracyLabel { get; set; } = "PRECISIÓN";

    /// <summary>
    /// Tolerancia de la dificultad, en semitonos (Fácil 2, Medio 1, Difícil 0): define el grosor de las notas y de
    /// las barras de voz.
    /// </summary>
    public int NoteTolerance { get; set; } = 2;

    /// <summary>Muestra la valoración breve al cerrar cada frase (junto a la tarjeta del jugador).</summary>
    public bool FeedbackEnabled { get; set; } = true;

    private const double FeedbackSeconds = 1.4;
    private readonly Dictionary<int, PhraseFeedback> _feedback = [];

    /// <summary>
    /// Registra mensajes nuevos, uno por jugador (el último reemplaza al anterior). Su vida se mide con la posición
    /// de la canción: un retroceso o un reinicio los apaga solos.
    /// </summary>
    public void ShowFeedback(IEnumerable<PhraseFeedback> messages)
    {
        foreach (var message in messages)
        {
            _feedback[message.Player] = message;
        }
    }

    public void ClearFeedback() => _feedback.Clear();

    private static Color RatingColor(PhraseRating rating) => rating switch
    {
        PhraseRating.Perfect => Color.FromRgb(0xFF, 0xC9, 0x33),
        PhraseRating.Excellent => Color.FromRgb(0x5B, 0xE5, 0x6B),
        PhraseRating.VeryGood => Color.FromRgb(0xA6, 0xF0, 0x6A),
        PhraseRating.Good => Color.FromRgb(0xF4, 0xF6, 0xFF),
        PhraseRating.Acceptable => Color.FromRgb(0xA9, 0xB1, 0xDA),
        PhraseRating.Poor => Color.FromRgb(0xFF, 0x8A, 0x2A),
        PhraseRating.Bad => Color.FromRgb(0xFF, 0x5A, 0x3C),
        _ => Color.FromRgb(0xFF, 0x4D, 0x4D),
    };

    /// <summary>
    /// Valoración de la última frase, a la derecha de la tarjeta (franja sin notas ni letra). Aparece con un
    /// pequeño rebote, se mantiene y se desvanece; la escala es una transformación, no otro tamaño de fuente.
    /// </summary>
    private void DrawFeedback(DrawingContext dc, StagePlayer player, Rect area, TimeSpan position)
    {
        if (!FeedbackEnabled || area.Width < 40 || !_feedback.TryGetValue(player.Number, out var message))
        {
            return;
        }

        var age = (position - message.ShownAt).TotalSeconds;
        if (age < 0 || age > FeedbackSeconds)
        {
            return;
        }

        var size = Math.Clamp(area.Height * 0.62, 24, 56);
        var text = Format(message.Text, PixelFace, size, White);
        var natural = text.WidthIncludingTrailingWhitespace;
        var fit = natural > area.Width ? area.Width / natural : 1.0;
        var pop = age < 0.13 ? 0.55 + 0.65 * (age / 0.13)
            : age < 0.25 ? 1.2 - 0.2 * ((age - 0.13) / 0.12)
            : 1.0;
        var opacity = age > FeedbackSeconds - 0.3 ? Math.Max(0, (FeedbackSeconds - age) / 0.3) : 1.0;

        var centerX = area.Right - natural * fit / 2;
        var centerY = area.Top + area.Height / 2;
        var scale = fit * pop;
        var at = new Point(centerX - natural / 2, centerY - text.Height / 2);

        dc.PushTransform(new ScaleTransform(scale, scale, centerX, centerY));
        dc.PushOpacity(opacity);
        DrawOutlined(dc, message.Text, PixelFace, size, BrushOf(RatingColor(message.Rating)), at, 2);
        dc.DrawRectangle(Ink, null, new Rect(at.X - 1, at.Y + text.Height - 1, natural + 2, 8));
        dc.DrawRectangle(BrushOf(player.Color), null, new Rect(at.X, at.Y + text.Height + 1, natural, 4));
        dc.Pop();
        dc.Pop();
    }

    public StageControl()
    {
        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        SizeChanged += (_, _) =>
        {
            _textCache.Clear();
            _spanCache.Clear();
        };
    }

    public void Show(Song? song, ISongClock? clock)
    {
        _song = song;
        _clock = clock;
        _feedback.Clear();
        if (song is not null)
        {
            _timing = new SongTiming(song.Bpm, song.GapMilliseconds);
        }

        InvalidateVisual();
    }

    private void OnRendering(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (!TranslucentBackground)
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));
        }

        if (_song is null || _clock is null || width < 50 || height < 50)
        {
            DrawCentered(dc, "Abrí una canción con Ctrl+O", width, height / 2 - 14, 28, Dim);
            return;
        }

        var position = _clock.Position;
        var beat = _timing.BeatAt(position);
        var voices = _song.Voices;
        var margin = Math.Max(12, height * 0.02);

        // Arriba solo el título, chico y sin fondo; el avatar y el puntaje de cada jugador van dentro de su carril.
        var titleHeight = Math.Clamp(height * 0.05, 26, 40);
        DrawSongTitle(dc, new Rect(margin, margin * 0.6, width - margin * 2, titleHeight));

        var progressHeight = 10.0;
        var top = margin * 0.6 + titleHeight + margin * 0.6;
        var bottom = height - margin - progressHeight - margin * 0.6;
        var panelHeight = (bottom - top - margin * (voices.Count - 1)) / voices.Count;

        for (var i = 0; i < voices.Count; i++)
        {
            var y = top + i * (panelHeight + margin);
            DrawVoice(dc, voices[i], showName: voices.Count > 1, voiceIndex: i,
                new Rect(margin, y, width - margin * 2, panelHeight), position, beat);
        }

        DrawProgress(dc, new Rect(margin, height - margin - progressHeight, width - margin * 2, progressHeight), position);
    }

    // ---- HUD -----------------------------------------------------------------------------------------------

    /// <summary>Artista y título en una línea, chicos, sin fondo ni marco (texto con contorno oscuro).</summary>
    private void DrawSongTitle(DrawingContext dc, Rect area)
    {
        var text = string.IsNullOrWhiteSpace(_song!.Artist) ? _song.Title : $"{_song.Artist} — {_song.Title}";
        var size = Math.Clamp(area.Height * 0.6, 14, 24);
        var natural = Format(text, PixelFace, size, White).WidthIncludingTrailingWhitespace;
        var fit = natural > area.Width && area.Width > 0 ? size * area.Width / natural : size;
        DrawOutlined(dc, text, PixelFace, fit, White, new Point(area.Left, area.Top), 2);
    }

    /// <summary>
    /// Tarjeta del jugador sobre su carril: retrato, nombre y puntaje (con la precisión a la derecha), con el marco
    /// del color del jugador.
    /// </summary>
    private void DrawPlayerCard(DrawingContext dc, Rect card, StagePlayer player)
    {
        var color = BrushOf(player.Color);
        var snapshot = player.Scorer?.Snapshot;
        var score = snapshot?.Total ?? 0;
        var accuracy = snapshot is { BeatsEvaluated: > 0 } s ? s.BeatsHit / (double)s.BeatsEvaluated : 0.0;

        DrawFrame(dc, card, Hud, color, notch: 4);

        const double pad = 5;
        var side = card.Height - pad * 2 - 2;
        DrawPortrait(dc, new Rect(card.Left + pad + 3, card.Top + pad + 1, side, side), player);
        var textLeft = card.Left + pad + 3 + side + 10;

        // precisión a la derecha
        var percent = $"{(int)Math.Round(accuracy * 100)}%";
        var percentSize = Math.Clamp(card.Height * 0.32, 16, 28);
        var percentText = Format(percent, PixelFace, percentSize, accuracy >= 0.5 ? Gold : Dim);
        var percentLeft = card.Right - percentText.WidthIncludingTrailingWhitespace - 10;
        dc.DrawText(percentText, new Point(percentLeft, card.Top + card.Height * 0.3));

        // nombre arriba y puntaje debajo, en el espacio que queda entre el retrato y la precisión
        var room = Math.Max(20, percentLeft - textLeft - 8);
        var nameSize = Math.Clamp(card.Height * 0.34, 18, 32);
        var nameWidth = Format(player.Name, PixelFace, nameSize, color).WidthIncludingTrailingWhitespace;
        var nameFit = nameWidth > room ? nameSize * room / nameWidth : nameSize;
        DrawOutlined(dc, player.Name, PixelFace, nameFit, color, new Point(textLeft, card.Top + 3), 1);

        var scoreSize = Math.Clamp(card.Height * 0.54, 30, 54);
        var scoreText = score.ToString("N0", CultureInfo.CurrentCulture);
        var scoreWidth = Format(scoreText, PixelFace, scoreSize, White).WidthIncludingTrailingWhitespace;
        var scoreFit = scoreWidth > room ? scoreSize * room / scoreWidth : scoreSize;
        DrawShadowed(dc, scoreText, PixelFace, scoreFit, White, new Point(textLeft, card.Top + card.Height * 0.38), 3);
    }

    private const int WaveColumns = 22;
    private const double WaveSeconds = 1.3;
    private readonly double[] _waveLevels = new double[WaveColumns];
    private readonly bool[] _waveVoiced = new bool[WaveColumns];

    /// <summary>
    /// Señal del micrófono del jugador, al lado de su tarjeta: la envolvente de la voz de los últimos ~1,3 s, de
    /// izquierda (vieja) a derecha (ahora), en columnas simétricas, sin marco ni fondo. Sale de los niveles
    /// que ya mide el detector de tono (no abre otra captura). Las columnas con tono van llenas; el ruido sin tono,
    /// más apagado. Sin micrófono asignado no se dibuja.
    /// </summary>
    private void DrawWave(DrawingContext dc, Rect area, StagePlayer player, TimeSpan position)
    {
        if (player.Timeline is not { } timeline || area.Width < 40)
        {
            return;
        }

        Array.Clear(_waveLevels);
        Array.Clear(_waveVoiced);
        _pitchBuffer.Clear();
        var from = position - TimeSpan.FromSeconds(WaveSeconds);
        timeline.CopyRange(from, position, _pitchBuffer);
        foreach (var sample in _pitchBuffer)
        {
            var column = (int)((sample.Time - from).TotalSeconds / WaveSeconds * WaveColumns);
            if (column is < 0 or >= WaveColumns)
            {
                continue;
            }

            // -55 dB (silencio práctico) a -10 dB (fuerte) → 0 a 1.
            var level = Math.Clamp((sample.LevelDb + 55) / 45, 0, 1);
            if (level > _waveLevels[column])
            {
                _waveLevels[column] = level;
            }

            _waveVoiced[column] |= sample.IsVoiced;
        }

        // Solo la onda, sin recuadro ni línea de base: cada columna lleva un contorno oscuro para leerse sobre el video.
        var inner = new Rect(area.Left + 4, area.Top + 6, area.Width - 8, area.Height - 12);
        var slot = inner.Width / WaveColumns;
        var barWidth = Math.Max(2, Math.Floor(slot) - 2);
        var middle = inner.Top + inner.Height / 2;

        for (var i = 0; i < WaveColumns; i++)
        {
            // Mínimo de 2 px (a cada lado de la línea) para que la señal se vea viva aun en silencio; pasos
            // de 2 px para conservar el aire de pixel art.
            var half = Math.Max(1, Math.Round(_waveLevels[i] * inner.Height / 2 / 2) * 2);
            var alpha = (byte)(_waveVoiced[i] ? 0xFF : _waveLevels[i] > 0 ? 0x90 : 0x40);
            var x = inner.Left + i * slot;
            dc.DrawRectangle(Ink, null, new Rect(x - 1, middle - half - 1, barWidth + 2, half * 2 + 2));
            dc.DrawRectangle(BrushOf(player.Color, alpha), null, new Rect(x, middle - half, barWidth, half * 2));
        }
    }

    private void DrawPortrait(DrawingContext dc, Rect rect, StagePlayer player)
    {
        dc.DrawRectangle(Ink, null, Inflate(rect, 2));
        dc.DrawRectangle(PortraitBack, null, rect);
        var tint = BrushOf(player.Color);
        if (player.Portrait is { } image)
        {
            var scale = Math.Max(rect.Width / image.Width, rect.Height / image.Height);
            var w = image.Width * scale;
            var h = image.Height * scale;
            dc.PushClip(new RectangleGeometry(rect));
            // pixel art: el retrato se amplía/reduce sin suavizado
            var drawing = new DrawingGroup();
            RenderOptions.SetBitmapScalingMode(drawing, BitmapScalingMode.NearestNeighbor);
            drawing.Children.Add(new ImageDrawing(image,
                new Rect(rect.Left + (rect.Width - w) / 2, rect.Top + (rect.Height - h) / 2, w, h)));
            dc.DrawDrawing(drawing);
            dc.Pop();
            return;
        }

        // silueta de cabeza y hombros, hecha con rectángulos
        var u = rect.Width / 8;
        dc.DrawRectangle(tint, null, new Rect(rect.Left + 2.5 * u, rect.Top + 1 * u, 3 * u, 3 * u));
        dc.DrawRectangle(tint, null, new Rect(rect.Left + 1 * u, rect.Top + 5 * u, 6 * u, 3 * u));
    }

    private void DrawProgress(DrawingContext dc, Rect bar, TimeSpan position)
    {
        if (Duration <= TimeSpan.Zero)
        {
            return;
        }

        var ratio = Math.Clamp(position.TotalSeconds / Duration.TotalSeconds, 0, 1);
        dc.DrawRectangle(ShadowBrush, null, new Rect(bar.Left + 2, bar.Top + 2, bar.Width, bar.Height));
        dc.DrawRectangle(Ink, null, bar);
        var inner = new Rect(bar.Left + 2, bar.Top + 2, Math.Max(0, bar.Width - 4), Math.Max(0, bar.Height - 4));
        dc.DrawRectangle(SlotFill, null, inner);
        // relleno en bloques, como una barra de energía de videojuego
        var block = 12.0;
        var filled = inner.Width * ratio;
        for (var x = 0.0; x < filled; x += block)
        {
            dc.DrawRectangle(Gold, null, new Rect(inner.Left + x, inner.Top, Math.Min(block - 2, filled - x), inner.Height));
        }
    }

    // ---- Voz -----------------------------------------------------------------------------------------------

    private readonly List<StagePlayer?> _lanes = [];

    /// <summary>
    /// Una voz de la canción: un carril de notas por jugador que la canta (cada uno con sus notas y su propia voz,
    /// como en UltraStar) y la letra compartida debajo. Sin jugadores en esa voz queda un carril solo con las notas.
    /// </summary>
    private void DrawVoice(DrawingContext dc, Voice voice, bool showName, int voiceIndex, Rect panel, TimeSpan position, double beat)
    {
        var lyricsHeight = Math.Min(panel.Height * 0.34, 150);
        var notes = new Rect(panel.Left, panel.Top, panel.Width, panel.Height - lyricsHeight - 10);

        _lanes.Clear();
        foreach (var player in Players)
        {
            if (player.VoiceIndex == voiceIndex)
            {
                _lanes.Add(player);
            }
        }

        if (_lanes.Count == 0)
        {
            _lanes.Add(null);
        }

        const double gap = 8;
        var laneHeight = (notes.Height - gap * (_lanes.Count - 1)) / _lanes.Count;
        for (var i = 0; i < _lanes.Count; i++)
        {
            var lane = new Rect(notes.Left, notes.Top + i * (laneHeight + gap), notes.Width, laneHeight);
            DrawLane(dc, voice, _lanes[i], _lanes.Count, lane, position, beat);
        }

        var lyrics = new Rect(panel.Left, notes.Bottom + 10, panel.Width, lyricsHeight);
        DrawLyrics(dc, voice, lyrics, beat);

        if (showName)
        {
            DrawOutlined(dc, voice.Name, PixelFace, 22, Dim, new Point(notes.Left + 12, notes.Top + 4), 2);
        }
    }

    /// <summary>Un carril: las notas de la voz y, encima, lo que canta <paramref name="player"/> (si hay).</summary>
    private void DrawLane(DrawingContext dc, Voice voice, StagePlayer? player, int laneCount, Rect lane,
        TimeSpan position, double beat)
    {
        _ = laneCount;

        // La tarjeta del jugador (retrato, nombre y puntaje) va arriba del carril; las notas, debajo.
        var notes = lane;
        if (player is not null)
        {
            var cardHeight = Math.Clamp(lane.Height * 0.3, 64, 96);
            var cardWidth = Math.Min(340, lane.Width * 0.5);
            DrawPlayerCard(dc, new Rect(lane.Left, lane.Top, cardWidth, cardHeight), player);
            var waveWidth = Math.Clamp(lane.Width * 0.12, 90, 170);
            var wave = new Rect(lane.Left + cardWidth + 8, lane.Top, waveWidth, cardHeight);
            DrawWave(dc, wave, player, position);
            var feedbackLeft = (player.Timeline is null ? lane.Left + cardWidth : wave.Right) + 16;
            DrawFeedback(dc, player,
                new Rect(feedbackLeft, lane.Top, Math.Max(0, lane.Right - feedbackLeft - 12), cardHeight),
                position);
            var used = cardHeight + 6;
            notes = new Rect(lane.Left, lane.Top + used, lane.Width, Math.Max(20, lane.Height - used));
        }

        // Sin fondo detrás de las notas: quedan directamente sobre el video (sus contornos las hacen legibles).

        var inner = new Rect(notes.Left + 6, notes.Top + 8, notes.Width - 12, notes.Height - 16);
        var nowX = inner.Left + inner.Width * NowLineFraction;
        var beatsPerSecond = _song!.Bpm * 4.0 / 60.0;
        var pixelsPerBeat = inner.Width * (1 - NowLineFraction) / (SecondsAhead * beatsPerSecond);

        // Filas: un semitono por fila, con un margen de dos filas para que las notas extremas no toquen el borde.
        var lowTone = voice.MinTone - 2;
        var highTone = voice.MaxTone + 2;
        var rows = highTone - lowTone + 1;
        var rowHeight = inner.Height / rows;
        // Como WorldParty: el grosor de la nota y de la barra cantada sigue a la dificultad. Una nota acepta
        // cantarse a ±tolerancia semitonos, así que ocupa (2·tolerancia + 1) filas: gruesa en Fácil, fina en Difícil.
        var noteHeight = Math.Clamp(rowHeight * (2 * NoteTolerance + 1) * 0.8, 14, Math.Max(18, inner.Height * 0.3));

        for (var tone = lowTone; tone <= highTone; tone += 12)
        {
            // Línea de referencia por octava (solo orientación visual; no es afinación absoluta).
            var y = inner.Bottom - (tone - lowTone + 0.5) * rowHeight;
            dc.DrawLine(Grid, new Point(inner.Left, y), new Point(inner.Right, y));
        }

        dc.PushClip(new RectangleGeometry(inner));
        foreach (var line in voice.Lines)
        {
            if (line.EndBeat < beat - SecondsBehind * beatsPerSecond - 1 ||
                line.StartBeat > beat + SecondsAhead * beatsPerSecond + 1)
            {
                continue;
            }

            foreach (var note in line.Notes)
            {
                // Las freestyle no tienen tono que cantar ni puntúan: no se dibujan (su letra sí se muestra).
                if (!note.IsScored)
                {
                    continue;
                }

                var x = nowX + (note.StartBeat - beat) * pixelsPerBeat;
                var w = Math.Max(note.Length * pixelsPerBeat - 3, 6);
                if (x + w < inner.Left || x > inner.Right)
                {
                    continue;
                }

                var tone = note.Kind == NoteKind.Rap || note.Kind == NoteKind.RapGolden
                    ? (lowTone + highTone) / 2.0
                    : note.Tone;
                var y = inner.Bottom - (tone - lowTone + 0.5) * rowHeight - noteHeight / 2;
                var isActive = note.StartBeat <= beat && beat < note.EndBeat;
                DrawNote(dc, new Rect(x, y, w, noteHeight), note.Kind, isActive);
            }
        }

        // Lo que canta el jugador de este carril (un carril por jugador: barras a altura completa, sin franjas).
        if (player is not null)
        {
            if (VoiceMode == VoiceDisplay.Beats)
            {
                DrawBeatBars(dc, player, 0, 1, inner, nowX, pixelsPerBeat, lowTone, highTone, rowHeight,
                    noteHeight, beat, beatsPerSecond, position);
            }
            else
            {
                DrawPitchTrail(dc, player, 0, 1, inner, nowX, pixelsPerBeat, lowTone, highTone,
                    rowHeight, position, beat);
            }
        }

        dc.Pop();
        DrawNowLine(dc, nowX, inner.Top, inner.Bottom);

    }

    /// <summary>Nota como "ranura": marco de color con relleno oscuro, esquinas escalonadas y sombra dura.</summary>
    private static void DrawNote(DrawingContext dc, Rect r, NoteKind kind, bool active)
    {
        var unit = Math.Max(2, Math.Round(r.Height / 7));
        var border = kind switch
        {
            NoteKind.Golden or NoteKind.RapGolden => Gold,
            NoteKind.Rap => RapBrush,
            _ => Player,
        };
        var fill = active ? ActiveFill : kind switch
        {
            NoteKind.Golden or NoteKind.RapGolden => SlotGoldFill,
            NoteKind.Rap => SlotRapFill,
            _ => SlotFill,
        };
        var notch = r.Width > unit * 4 ? unit : 0;

        // Solo rectángulos: son el camino rápido de WPF (los polígonos nuevos en cada cuadro se vuelven a triangular).
        FillStepped(dc, ShadowBrush, r.Left + unit, r.Top + unit, r.Width, r.Height, notch, unit);
        FillStepped(dc, Ink, r.Left - 1, r.Top - 1, r.Width + 2, r.Height + 2, notch, unit);
        FillStepped(dc, active ? White : border, r.Left, r.Top, r.Width, r.Height, notch, unit);
        if (r.Width > unit * 2 + 2 && r.Height > unit * 2 + 2)
        {
            FillStepped(dc, fill, r.Left + unit, r.Top + unit, r.Width - 2 * unit, r.Height - 2 * unit,
                Math.Max(0, notch - unit), unit);
        }
    }

    /// <summary>
    /// Rectángulo con esquinas en escalera, hecho con rectángulos superpuestos: cada uno es más angosto y más
    /// alto que el anterior, y su unión es la silueta escalonada (mismo resultado que <see cref="PixelFrame.Stepped"/>).
    /// </summary>
    private static void FillStepped(DrawingContext dc, Brush brush, double x, double y, double w, double h, double notch, double unit)
    {
        var steps = unit > 0 ? (int)Math.Round(notch / unit) : 0;
        if (steps <= 0 || w <= 2 * notch || h <= 2 * notch)
        {
            dc.DrawRectangle(brush, null, new Rect(x, y, w, h));
            return;
        }

        for (var k = 0; k <= steps; k++)
        {
            var inset = (steps - k) * unit;
            dc.DrawRectangle(brush, null, new Rect(x + inset, y + k * unit, w - 2 * inset, h - 2 * k * unit));
        }
    }

    private static void DrawNowLine(DrawingContext dc, double x, double top, double bottom)
    {
        dc.DrawRectangle(Ink, null, new Rect(x - 3, top, 6, bottom - top));
        dc.DrawRectangle(Gold, null, new Rect(x - 1.5, top, 3, bottom - top));
        // rombo pixel arriba y abajo de la línea
        dc.DrawRectangle(Ink, null, new Rect(x - 7, top - 1, 14, 6));
        dc.DrawRectangle(Gold, null, new Rect(x - 5, top, 10, 4));
    }

    /// <summary>
    /// Voz por beat, como UltraStar: beats contiguos en el mismo tono forman una barra. Si el beat acertó, la
    /// barra queda sobre la nota a altura completa; si no, va al tono real (a ±6 semitonos de la nota) con
    /// el 65 % de la altura. Solo hay barras sobre las notas; entre notas no se dibuja nada. Sale de la misma
    /// evaluación que el puntaje, así que llega con el retraso de asentamiento.
    /// </summary>
    private void DrawBeatBars(DrawingContext dc, StagePlayer player, int lane, int lanes, Rect notes, double nowX,
        double pixelsPerBeat, int lowTone, int highTone, double rowHeight, double noteHeight, double beat,
        double beatsPerSecond, TimeSpan position)
    {
        if (player.Scorer is not { } scorer)
        {
            return;
        }

        // Con varios jugadores en la misma voz, la altura de la nota se reparte en franjas (una por jugador).
        var strip = Math.Max(3, noteHeight / lanes);
        var stripOffset = (lane - (lanes - 1) / 2.0) * strip;
        var hitBrush = BrushOf(player.Color);
        var missBrush = BrushOf(player.Color, 0xB0);

        // Lo ya evaluado más lo provisional de los beats cuya voz ya llegó (la barra aparece sin esperar el asentamiento).
        _barSource.Clear();
        _barSource.AddRange(scorer.Results);
        if (player.Timeline is not null)
        {
            _barSource.AddRange(scorer.Preview(player.Timeline, position));
        }

        var from = (int)Math.Floor(beat - SecondsBehind * beatsPerSecond) - 1;
        foreach (var bar in VoiceBars.Build(_barSource, from))
        {
            var left = nowX + (bar.StartBeat - beat) * pixelsPerBeat;
            var right = Math.Min(nowX + (bar.EndBeat - beat) * pixelsPerBeat - 3, nowX);
            if (right <= left)
            {
                continue;
            }

            // El rap se dibuja a media altura del carril, igual que sus notas.
            var tone = bar.Rap ? (lowTone + highTone) / 2.0 : bar.Tone;
            var height = bar.Hit ? strip : strip * MissHeightFraction;
            var y = notes.Bottom - (tone - lowTone + 0.5) * rowHeight + stripOffset;
            var rect = new Rect(left, y - height / 2, Math.Max(right - left, 6), height);
            var unit = Math.Max(2, Math.Round(rect.Height / 7));
            var barNotch = rect.Width > unit * 4 ? unit : 0;
            FillStepped(dc, Ink, rect.Left - 1, rect.Top - 1, rect.Width + 2, rect.Height + 2, barNotch, unit);
            FillStepped(dc, bar.Hit ? hitBrush : missBrush, rect.Left, rect.Top, rect.Width, rect.Height, barNotch, unit);
        }
    }

    /// <summary>
    /// Cada tono detectado se dibuja como un segmento que cubre el intervalo de muestras analizadas,
    /// ubicado por el instante de esas muestras en la canción. Solo visualización: no hay puntuación.
    /// </summary>
    private void DrawPitchTrail(DrawingContext dc, StagePlayer player, int lane, int lanes, Rect notes, double nowX,
        double pixelsPerBeat, int lowTone, int highTone, double rowHeight, TimeSpan position, double beat)
    {
        if (player.Timeline is not { } pitch)
        {
            return;
        }

        // Varios jugadores en la misma voz: cada estela es más fina y se corre en su franja.
        var thickness = Math.Max(3.0, 6.0 / lanes);
        var stripOffset = (lane - (lanes - 1) / 2.0) * thickness;

        // La voz llega con retraso: se pide lo que corresponde a la ventana visible desplazada ese retraso y se
        // dibuja ese tiempo antes, de modo que quede sobre la nota que se estaba cantando.
        var shift = player.Delay;
        _pitchBuffer.Clear();
        pitch.CopyRange(position - TimeSpan.FromSeconds(SecondsBehind + 0.5) + shift, position + TimeSpan.FromSeconds(0.5) + shift, _pitchBuffer);

        foreach (var sample in _pitchBuffer)
        {
            if (!sample.IsVoiced)
            {
                continue;
            }

            // Solo para dibujar: el tono se lleva a la octava que entra en el carril, cualquiera sea el
            // origen de tonos del archivo (C4 = 0 o número MIDI directo).
            if (MusicalPitch.FoldIntoRange(MusicalPitch.FromFrequency(sample.Frequency).Midi - 60, lowTone, highTone)
                is not { } tone)
            {
                continue;
            }

            var startX = nowX + (_timing.BeatAt(sample.Start - shift) - beat) * pixelsPerBeat;
            var endX = nowX + (_timing.BeatAt(sample.End - shift) - beat) * pixelsPerBeat;
            var y = notes.Bottom - (tone - lowTone + 0.5) * rowHeight + stripOffset;
            var alpha = (byte)(110 + 145 * Math.Clamp(sample.Confidence, 0, 1));
            dc.DrawRectangle(BrushOf(player.Color, alpha), null,
                new Rect(startX, y - thickness / 2, Math.Max(endX - startX, 3), thickness));
        }
    }

    // ---- Letra ---------------------------------------------------------------------------------------------

    private void DrawLyrics(DrawingContext dc, Voice voice, Rect area, double beat)
    {
        var beatsPerSecond = _song!.Bpm * 4.0 / 60.0;

        // Arriba va la frase que se canta o, cuando termina, la que viene: así hay un único indicador y vive
        // siempre en la fila de arriba. Abajo, la siguiente.
        var index = LyricCue.DisplayIndex(voice, beat, holdBeats: 0.35 * beatsPerSecond);
        if (index < 0)
        {
            return;
        }

        var current = voice.Lines[index];
        // Jersey 15 es angosta y baja: se usa más grande que una tipografía común para el mismo peso visual.
        var size = Math.Clamp(area.Height * 0.55, 26, 76);
        var text = Display(current);
        var white = Format(text, PixelFace, size, White);
        var gold = Format(text, PixelFace, size, Gold);
        FitWidth(white, size, area.Width - 40);
        FitWidth(gold, size, area.Width - 40);
        var x = area.Left + (area.Width - white.WidthIncludingTrailingWhitespace) / 2;

        // Sin recuadro: directo sobre el video, con contorno oscuro (copias desplazadas del texto: mucho más
        // barato que trazar la geometría de las letras en cada cuadro).
        var ring = Math.Max(2, Math.Round(size / 14));
        DrawTextRing(dc, text, size, white, new Point(x, area.Top), ring);
        dc.DrawText(white, new Point(x, area.Top));

        // Relleno: sigue el tiempo real de cada sílaba (su inicio y fin) sobre el tramo que ocupa en el texto.
        var spans = SpansOf(white, current);
        var times = new (double, double)[current.Notes.Count];
        for (var i = 0; i < times.Length; i++)
        {
            times[i] = (current.Notes[i].StartBeat, current.Notes[i].EndBeat);
        }

        var fill = LyricCue.FillX(times, spans, beat, textStart: 0);
        if (fill > 0)
        {
            dc.PushClip(new RectangleGeometry(new Rect(x - 2, area.Top - 4, fill + 2, white.Height + 8)));
            dc.DrawText(gold, new Point(x, area.Top));
            dc.Pop();
        }

        // Indicador sutil: línea fina bajo la frase de arriba. Antes de empezar, un marcador se acerca al inicio
        // y llega justo cuando toca cantar; después, la misma línea se llena a la par del texto.
        var lineWidth = Math.Max(1, white.Width);
        var cueY = area.Top + white.Height + 2;
        if (LyricCue.Approach((current.StartBeat - beat) / beatsPerSecond) is { } approach)
        {
            DrawCue(dc, x, cueY, lineWidth, approach, null);
        }
        else if (beat >= current.StartBeat)
        {
            DrawCue(dc, x, cueY, lineWidth, null, Math.Clamp(fill / lineWidth, 0, 1));
        }

        if (index + 1 < voice.Lines.Count)
        {
            var nextText = Display(voice.Lines[index + 1]);
            var nextSize = size * 0.7;
            var next = Format(nextText, PixelFace, nextSize, Dim);
            FitWidth(next, nextSize, area.Width - 40);
            var nx = area.Left + (area.Width - next.WidthIncludingTrailingWhitespace) / 2;
            var ny = area.Top + size * 1.2;
            DrawTextRing(dc, nextText, nextSize, next, new Point(nx, ny), Math.Max(2, Math.Round(nextSize / 16)));
            dc.DrawText(next, new Point(nx, ny));
        }
    }

    /// <summary>Tramo horizontal de cada sílaba dentro de su frase ya maquetada (en caché por texto).</summary>
    private LyricCue.Span[] SpansOf(FormattedText text, Line line)
    {
        if (_spanCache.TryGetValue(text, out var cached) && cached.Length == line.Notes.Count)
        {
            return cached;
        }

        var spans = new LyricCue.Span[line.Notes.Count];
        var offset = 0;
        var last = 0.0;
        for (var i = 0; i < spans.Length; i++)
        {
            var length = Display(line.Notes[i]).Length;
            if (length > 0 && text.BuildHighlightGeometry(new Point(0, 0), offset, length) is { } geometry)
            {
                var bounds = geometry.Bounds;
                spans[i] = new LyricCue.Span(bounds.Left, bounds.Right);
                last = bounds.Right;
            }
            else
            {
                spans[i] = new LyricCue.Span(last, last);
            }

            offset += length;
        }

        _spanCache[text] = spans;
        return spans;
    }

    private readonly Dictionary<FormattedText, LyricCue.Span[]> _spanCache = [];

    private static readonly Brush CueTrack = Freeze(new SolidColorBrush(Color.FromArgb(0x5A, 0xF4, 0xF6, 0xFF)));

    /// <summary>Línea fina bajo la frase; con <paramref name="approach"/> dibuja el marcador, con <paramref name="sung"/> el avance.</summary>
    private static void DrawCue(DrawingContext dc, double left, double y, double width, double? approach, double? sung)
    {
        if (width < 8)
        {
            return;
        }

        dc.DrawRectangle(CueTrack, null, new Rect(left, y, width, 2));
        if (sung is { } fraction)
        {
            dc.DrawRectangle(Gold, null, new Rect(left, y, width * fraction, 2));
        }

        if (approach is { } p)
        {
            // el marcador viene desde la izquierda y toca el inicio de la línea cuando empieza la frase
            var x = left - 150 * (1 - p);
            dc.DrawRectangle(Ink, null, new Rect(x - 6, y - 5, 12, 12));
            dc.DrawRectangle(Gold, null, new Rect(x - 4, y - 3, 8, 8));
        }
    }

    /// <summary>Contorno duro: el mismo texto en tinta oscura desplazado en las ocho direcciones.</summary>
    private void DrawTextRing(DrawingContext dc, string text, double size, FormattedText fitted, Point at, double radius)
    {
        var ink = Format(text, PixelFace, size, Ink);
        if (ink.WidthIncludingTrailingWhitespace > fitted.WidthIncludingTrailingWhitespace + 0.5)
        {
            ink.SetFontSize(size * fitted.WidthIncludingTrailingWhitespace / ink.WidthIncludingTrailingWhitespace);
        }

        // Cuatro copias en diagonal: contorno suficiente para leerse sobre el video y la mitad de trabajo que ocho
        // (con ocho el escenario perdía cuadros en canciones de letra larga).
        dc.DrawText(ink, new Point(at.X - radius, at.Y - radius));
        dc.DrawText(ink, new Point(at.X + radius, at.Y - radius));
        dc.DrawText(ink, new Point(at.X - radius, at.Y + radius));
        dc.DrawText(ink, new Point(at.X + radius, at.Y + radius));
    }

    /// <summary>"~" marca una nota sostenida en el TXT; no es parte de la letra que se canta.</summary>
    private static string Display(Note note) => note.Text.Trim() == "~" ? string.Empty : note.Text;

    private static string Display(Line line) => string.Concat(line.Notes.Select(Display));

    // ---- Utilidades de dibujo ------------------------------------------------------------------------------

    /// <summary>Marco de esquinas escalonadas con borde de color, relleno y (opcional) sombra dura.</summary>
    private static void DrawFrame(DrawingContext dc, Rect r, Brush fill, Brush border, double notch, bool shadow = true)
    {
        if (r.Width < notch * 3 || r.Height < notch * 3)
        {
            return;
        }

        const double unit = 3;
        if (shadow)
        {
            FillStepped(dc, ShadowBrush, r.Left + unit, r.Top + unit, r.Width, r.Height, notch, unit);
        }

        // Anillo e interior en caché (congelados): el tamaño de estos marcos casi no cambia entre cuadros, y una
        // geometría congelada se triangula una sola vez. Con relleno translúcido el video se ve por dentro.
        var key = (Math.Round(r.Width), Math.Round(r.Height), notch);
        if (!FrameCache.TryGetValue(key, out var shapes))
        {
            if (FrameCache.Count > 48)
            {
                FrameCache.Clear();
            }

            var w = key.Item1;
            var h = key.Item2;
            var outer = PixelFrame.Stepped(0, 0, w, h, notch, unit);
            var inner = PixelFrame.Stepped(unit, unit, w - 2 * unit, h - 2 * unit, Math.Max(0, notch - unit), unit);
            var ring = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { outer, inner } };
            ring.Freeze();
            shapes = (ring, inner);
            FrameCache[key] = shapes;
        }

        dc.PushTransform(new TranslateTransform(Math.Round(r.Left), Math.Round(r.Top)));
        dc.DrawGeometry(border, null, shapes.Ring);
        dc.DrawGeometry(fill, null, shapes.Inner);
        dc.Pop();
    }

    private static readonly Dictionary<(double, double, double), (Geometry Ring, Geometry Inner)> FrameCache = [];
    private static readonly Brush PortraitBack = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x10, 0x40)));

    private static Rect Inflate(Rect r, double amount) =>
        new(r.Left - amount, r.Top - amount, r.Width + 2 * amount, r.Height + 2 * amount);

    // Los textos del HUD y la letra se repiten cuadro tras cuadro: se reutilizan en vez de formatearlos de nuevo.
    private readonly Dictionary<(string, Typeface, double, Brush), FormattedText> _textCache = [];

    private FormattedText Format(string text, Typeface face, double size, Brush brush)
    {
        var key = (text, face, size, brush);
        if (!_textCache.TryGetValue(key, out var formatted))
        {
            if (_textCache.Count > 160)
            {
                _textCache.Clear();
                _spanCache.Clear();
            }

            formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size,
                brush, Dpi());
            _textCache[key] = formatted;
        }

        return formatted;
    }

    /// <summary>Texto con contorno oscuro duro (ocho copias desplazadas) para leerse sobre cualquier fondo.</summary>
    private void DrawOutlined(DrawingContext dc, string text, Typeface face, double size, Brush brush, Point at, double outline)
    {
        var ink = Format(text, face, size, Ink);
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (dx != 0 || dy != 0)
                {
                    dc.DrawText(ink, new Point(at.X + dx * outline, at.Y + dy * outline));
                }
            }
        }

        dc.DrawText(Format(text, face, size, brush), at);
    }

    /// <summary>Texto con sombra dura desplazada abajo a la derecha.</summary>
    private void DrawShadowed(DrawingContext dc, string text, Typeface face, double size, Brush brush, Point at, double offset)
    {
        dc.DrawText(Format(text, face, size, Ink), new Point(at.X + offset, at.Y + offset));
        dc.DrawText(Format(text, face, size, brush), at);
    }

    private static void FitWidth(FormattedText text, double fontSize, double maxWidth)
    {
        if (text.WidthIncludingTrailingWhitespace > maxWidth && maxWidth > 0)
        {
            text.SetFontSize(fontSize * maxWidth / text.WidthIncludingTrailingWhitespace);
        }
    }

    private void DrawCentered(DrawingContext dc, string message, double width, double y, double size, Brush brush)
    {
        var text = Format(message, BodyPlain, size, brush);
        dc.DrawText(text, new Point((width - text.Width) / 2, y));
    }

    private double Dpi() => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private static Typeface ResolvePixelFace()
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource("PixelFont") is FontFamily family)
            {
                return new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            }
        }
        catch (InvalidOperationException)
        {
            // sin recursos de la aplicación (pruebas, diseñador): tipografía común
        }

        return new Typeface("Segoe UI Semibold");
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FreezePen(Pen pen)
    {
        pen.Brush.Freeze();
        pen.Freeze();
        return pen;
    }
}
