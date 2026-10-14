// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Globalization;
using System.Windows;
using System.Windows.Media;
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
/// Dibuja notas y letra de una canción según la posición de un <see cref="ISongClock"/>.
/// No conoce el reproductor de audio: solo consulta la posición.
/// </summary>
public sealed class StageControl : FrameworkElement
{
    private const double SecondsBehind = 2.0;
    private const double SecondsAhead = 6.0;
    private const double NowLineFraction = 0.22;

    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1A)));
    private static readonly Brush Lane = Freeze(new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x25)));
    private static readonly Pen Grid = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), 1));
    private static readonly Pen NowPen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), 2));
    private static readonly Brush Normal = Freeze(new SolidColorBrush(Color.FromRgb(0x4F, 0x8F, 0xF0)));
    private static readonly Brush Golden = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xC1, 0x2E)));
    private static readonly Brush Rap = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x3E)));
    private static readonly Brush Free = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0x9A, 0xA3, 0xB2)));
    private static readonly Brush Active = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly Brush Sung = Freeze(new SolidColorBrush(Color.FromRgb(0x6F, 0xE0, 0x8E)));
    private static readonly Brush Pending = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)));
    private static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0x9E)));

    private static readonly Brush VoiceHit = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x5E, 0xA8)));
    private static readonly Brush VoiceMiss = Freeze(new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0x5E, 0xA8)));

    // UltraStar dibuja un fallo al 65 % de la altura de un acierto.
    private const double MissHeightFraction = 0.65;

    private static readonly Typeface Face = new("Segoe UI");

    private readonly List<PitchSample> _pitchBuffer = [];

    private Song? _song;
    private ISongClock? _clock;
    private PitchTimeline? _pitch;
    private SongTiming _timing;

    /// <summary>Cómo se dibuja la voz del jugador; se puede cambiar en cualquier momento.</summary>
    public VoiceDisplay VoiceMode { get; set; } = VoiceDisplay.Beats;

    /// <summary>Evaluación por beat de la voz 0; sin ella, el modo por beat no dibuja nada.</summary>
    public VoiceScorer? Scorer { get; set; }

    /// <summary>
    /// Retardo con el que llega la voz respecto de la canción (el mismo que usa el puntaje). La estela cruda se
    /// dibuja ese tiempo antes para que quede alineada con la nota que se estaba cantando.
    /// </summary>
    public TimeSpan VoiceDelay { get; set; }

    private readonly List<BeatResult> _barSource = [];

    public StageControl()
    {
        Loaded += (_, _) => System.Windows.Media.CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
        SnapsToDevicePixels = true;
    }

    /// <param name="pitch">Tonos detectados en la entrada activa; se dibujan sobre la primera voz.</param>
    public void Show(Song? song, ISongClock? clock, PitchTimeline? pitch = null)
    {
        _song = song;
        _clock = clock;
        _pitch = pitch;
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
        dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));

        if (_song is null || _clock is null || width < 50 || height < 50)
        {
            DrawCentered(dc, "Abrí una canción con Ctrl+O", width, height / 2 - 14, 28, Dim);
            return;
        }

        var position = _clock.Position;
        var beat = _timing.BeatAt(position);
        var voices = _song.Voices;
        var margin = Math.Max(12, height * 0.02);
        var panelHeight = (height - margin * (voices.Count + 1)) / voices.Count;

        for (var i = 0; i < voices.Count; i++)
        {
            var top = margin + i * (panelHeight + margin);
            DrawVoice(dc, voices[i], showName: voices.Count > 1, drawPitch: i == 0,
                new Rect(margin, top, width - margin * 2, panelHeight), position, beat);
        }
    }

    private void DrawVoice(DrawingContext dc, Voice voice, bool showName, bool drawPitch, Rect panel, TimeSpan position, double beat)
    {
        dc.DrawRoundedRectangle(Lane, null, panel, 8, 8);

        var lyricsHeight = Math.Min(panel.Height * 0.38, 150);
        var notes = new Rect(panel.Left, panel.Top + 8, panel.Width, panel.Height - lyricsHeight - 16);
        var nowX = notes.Left + notes.Width * NowLineFraction;
        var beatsPerSecond = _song!.Bpm * 4.0 / 60.0;
        var pixelsPerBeat = notes.Width * (1 - NowLineFraction) / (SecondsAhead * beatsPerSecond);

        // Filas: un semitono por fila, con un margen de dos filas para que las notas extremas no toquen el borde.
        var lowTone = voice.MinTone - 2;
        var highTone = voice.MaxTone + 2;
        var rows = highTone - lowTone + 1;
        var rowHeight = notes.Height / rows;
        var noteHeight = Math.Clamp(rowHeight * 0.8, 6, 26);

        for (var tone = lowTone; tone <= highTone; tone += 12)
        {
            // Línea de referencia por octava (solo orientación visual; no es afinación absoluta).
            var y = notes.Bottom - (tone - lowTone + 0.5) * rowHeight;
            dc.DrawLine(Grid, new Point(notes.Left, y), new Point(notes.Right, y));
        }

        dc.PushClip(new RectangleGeometry(notes));
        foreach (var line in voice.Lines)
        {
            if (line.EndBeat < beat - SecondsBehind * beatsPerSecond - 1 ||
                line.StartBeat > beat + SecondsAhead * beatsPerSecond + 1)
            {
                continue;
            }

            foreach (var note in line.Notes)
            {
                var x = nowX + (note.StartBeat - beat) * pixelsPerBeat;
                var w = Math.Max(note.Length * pixelsPerBeat - 2, 4);
                if (x + w < notes.Left || x > notes.Right)
                {
                    continue;
                }

                var tone = note.Kind == NoteKind.Freestyle || note.Kind == NoteKind.Rap || note.Kind == NoteKind.RapGolden
                    ? (lowTone + highTone) / 2.0
                    : note.Tone;
                var y = notes.Bottom - (tone - lowTone + 0.5) * rowHeight - noteHeight / 2;
                var isActive = note.StartBeat <= beat && beat < note.EndBeat;
                dc.DrawRoundedRectangle(isActive ? Active : BrushFor(note.Kind), null,
                    new Rect(x, y, w, noteHeight), noteHeight / 3, noteHeight / 3);
            }
        }

        if (drawPitch)
        {
            if (VoiceMode == VoiceDisplay.Beats)
            {
                DrawBeatBars(dc, notes, nowX, pixelsPerBeat, lowTone, highTone, rowHeight, noteHeight, beat, beatsPerSecond, position);
            }
            else
            {
                DrawPitchTrail(dc, notes, nowX, pixelsPerBeat, lowTone, highTone, rowHeight, position, beat);
            }
        }

        dc.Pop();
        dc.DrawLine(NowPen, new Point(nowX, notes.Top), new Point(nowX, notes.Bottom));

        var lyrics = new Rect(panel.Left, notes.Bottom + 8, panel.Width, lyricsHeight);
        DrawLyrics(dc, voice, lyrics, beat);

        if (showName)
        {
            var name = new FormattedText(voice.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Face, 16, Dim, Dpi());
            dc.DrawText(name, new Point(panel.Left + 10, panel.Top + 6));
        }
    }

    /// <summary>
    /// Voz por beat, como UltraStar: beats contiguos en el mismo tono forman una barra. Si el beat acertó, la
    /// barra queda sobre la nota a altura completa; si no, va al tono real (a ±6 semitonos de la nota) con
    /// el 65 % de la altura. Solo hay barras sobre las notas; entre notas no se dibuja nada. Sale de la misma
    /// evaluación que el puntaje, así que llega con el retraso de asentamiento.
    /// </summary>
    private void DrawBeatBars(DrawingContext dc, Rect notes, double nowX, double pixelsPerBeat,
        int lowTone, int highTone, double rowHeight, double noteHeight, double beat, double beatsPerSecond,
        TimeSpan position)
    {
        if (Scorer is not { } scorer)
        {
            return;
        }

        // Lo ya evaluado más lo provisional de los beats cuya voz ya llegó (la barra aparece sin esperar el asentamiento).
        _barSource.Clear();
        _barSource.AddRange(scorer.Results);
        if (_pitch is not null)
        {
            _barSource.AddRange(scorer.Preview(_pitch, position));
        }

        var from = (int)Math.Floor(beat - SecondsBehind * beatsPerSecond) - 1;
        foreach (var bar in VoiceBars.Build(_barSource, from))
        {
            var left = nowX + (bar.StartBeat - beat) * pixelsPerBeat;
            var right = Math.Min(nowX + (bar.EndBeat - beat) * pixelsPerBeat - 2, nowX);
            if (right <= left)
            {
                continue;
            }

            // El rap se dibuja a media altura del carril, igual que sus notas.
            var tone = bar.Rap ? (lowTone + highTone) / 2.0 : bar.Tone;
            var height = bar.Hit ? noteHeight : noteHeight * MissHeightFraction;
            var y = notes.Bottom - (tone - lowTone + 0.5) * rowHeight;
            dc.DrawRoundedRectangle(bar.Hit ? VoiceHit : VoiceMiss, null,
                new Rect(left, y - height / 2, Math.Max(right - left, 4), height), height / 3, height / 3);
        }
    }

    /// <summary>
    /// Cada tono detectado se dibuja como un segmento que cubre el intervalo de muestras analizadas,
    /// ubicado por el instante de esas muestras en la canción. Solo visualización: no hay puntuación.
    /// </summary>
    private void DrawPitchTrail(DrawingContext dc, Rect notes, double nowX, double pixelsPerBeat,
        int lowTone, int highTone, double rowHeight, TimeSpan position, double beat)
    {
        if (_pitch is null)
        {
            return;
        }

        // La voz llega con retraso: se pide lo que corresponde a la ventana visible desplazada ese retraso y se
        // dibuja ese tiempo antes, de modo que quede sobre la nota que se estaba cantando.
        var shift = VoiceDelay;
        _pitchBuffer.Clear();
        _pitch.CopyRange(position - TimeSpan.FromSeconds(SecondsBehind + 0.5) + shift, position + TimeSpan.FromSeconds(0.5) + shift, _pitchBuffer);

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
            var y = notes.Bottom - (tone - lowTone + 0.5) * rowHeight;
            var alpha = (byte)(110 + 145 * Math.Clamp(sample.Confidence, 0, 1));
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0x5E, 0xA8));
            brush.Freeze();
            dc.DrawRoundedRectangle(brush, null, new Rect(startX, y - 3, Math.Max(endX - startX, 3), 6), 3, 3);
        }
    }

    private void DrawLyrics(DrawingContext dc, Voice voice, Rect area, double beat)
    {
        var index = voice.LineIndexAt(beat);
        if (index < 0)
        {
            return;
        }

        var current = voice.Lines[index];
        var size = Math.Clamp(area.Height * 0.42, 18, 56);
        var text = Display(current);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face, size, Pending, Dpi());

        // Resalta lo ya cantado: las notas cuyo inicio ya pasó.
        var offset = 0;
        foreach (var note in current.Notes)
        {
            var length = Display(note).Length;
            if (note.StartBeat <= beat && length > 0)
            {
                formatted.SetForegroundBrush(Sung, offset, length);
            }

            offset += length;
        }

        FitWidth(formatted, size, area.Width - 24);
        dc.DrawText(formatted, new Point(area.Left + (area.Width - formatted.WidthIncludingTrailingWhitespace) / 2, area.Top));

        if (index + 1 < voice.Lines.Count)
        {
            var next = new FormattedText(Display(voice.Lines[index + 1]), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, Face, size * 0.62, Dim, Dpi());
            FitWidth(next, size * 0.62, area.Width - 24);
            dc.DrawText(next, new Point(area.Left + (area.Width - next.WidthIncludingTrailingWhitespace) / 2,
                area.Top + size * 1.35));
        }
    }

    /// <summary>"~" marca una nota sostenida en el TXT; no es parte de la letra que se canta.</summary>
    private static string Display(Note note) => note.Text.Trim() == "~" ? string.Empty : note.Text;

    private static string Display(Line line) => string.Concat(line.Notes.Select(Display));

    private static void FitWidth(FormattedText text, double fontSize, double maxWidth)
    {
        if (text.WidthIncludingTrailingWhitespace > maxWidth && maxWidth > 0)
        {
            text.SetFontSize(fontSize * maxWidth / text.WidthIncludingTrailingWhitespace);
        }
    }

    private void DrawCentered(DrawingContext dc, string message, double width, double y, double size, Brush brush)
    {
        var text = new FormattedText(message, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face, size, brush, Dpi());
        dc.DrawText(text, new Point((width - text.Width) / 2, y));
    }

    private double Dpi() => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private static Brush BrushFor(NoteKind kind) => kind switch
    {
        NoteKind.Golden => Golden,
        NoteKind.Rap or NoteKind.RapGolden => Rap,
        NoteKind.Freestyle => Free,
        _ => Normal,
    };

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
