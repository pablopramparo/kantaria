// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kantor.App.Pixel;
using Kantor.Core.Results;

namespace Kantor.App.Results;

/// <summary>
/// Medidas de la presentación de un jugador: personaje suelto (zona de <see cref="ZoneWidth"/> ×
/// <see cref="ZoneHeight"/>) y el panel con nombre, puntaje, sello y comentario; a su derecha, o debajo si
/// <see cref="Vertical"/> (3 jugadores en fila).
/// </summary>
public sealed record PanelMetrics(
    double Width, double ZoneWidth, double ZoneHeight, double Name, double Score, double Points, double Stamp, double Comment,
    bool Vertical = false)
{
    public static PanelMetrics For(int players) => players switch
    {
        <= 1 => new(920, 320, 470, 56, 150, 28, 58, 38),
        2 => new(620, 190, 360, 42, 88, 22, 40, 28),
        3 => new(410, 410, 190, 34, 70, 20, 32, 24, Vertical: true),
        _ => new(620, 170, 220, 32, 60, 18, 30, 22),
    };
}

/// <summary>
/// Presentación de un jugador: personaje de cuerpo entero sin marco, integrado al escenario, y un panel con
/// nombre, puntaje, sello de rango y comentario. Sirve tanto para un jugador (centrado) como para cada
/// participante de la ceremonia.
/// </summary>
public sealed class PlayerResultPanel : StackPanel
{
    private static readonly Brush Ink = Frozen(0x05, 0x07, 0x18);
    private static readonly Brush PanelFill = Frozen(0x10, 0x14, 0x2E);
    private static readonly Brush Text = Frozen(0xF4, 0xF6, 0xFF);
    private static readonly Brush Dim = Frozen(0x8A, 0x91, 0xB8);
    private static readonly Brush Gold = Frozen(0xFF, 0xC9, 0x33);
    private static readonly Brush Dark = Frozen(0x2A, 0x15, 0x00);
    private static readonly FontFamily Pixel = (FontFamily)Application.Current.Resources["PixelFont"];

    private readonly PlayerResult _result;
    private readonly TranslateTransform _slide = new();
    private readonly TranslateTransform _characterSlide = new();
    private readonly PixelCharacter _character;
    private readonly PixelText _scoreText;
    private readonly FrameworkElement _stamp;
    private readonly ScaleTransform _stampScale = new(1, 1);
    private readonly FrameworkElement _comment;
    private readonly FrameworkElement? _tag;
    private DispatcherTimer? _landTimer;
    private DispatcherTimer? _doneTimer;

    public static readonly DependencyProperty ShownScoreProperty = DependencyProperty.Register(nameof(ShownScore),
        typeof(double), typeof(PlayerResultPanel), new PropertyMetadata(0.0, (d, e) =>
        {
            var panel = (PlayerResultPanel)d;
            panel._scoreText.Text = ((int)Math.Round((double)e.NewValue)).ToString("N0");
        }));

    /// <summary>Se dispara cuando el sello "cae" sobre la pantalla (para lanzar el confeti).</summary>
    public event EventHandler? StampLanded;

    public double ShownScore
    {
        get => (double)GetValue(ShownScoreProperty);
        set => SetValue(ShownScoreProperty, value);
    }

    public ResultRank Rank => _result.Rank;

    /// <summary>La secuencia de entrada sigue en curso (se puede saltear).</summary>
    public bool IsAnimating { get; private set; }

    public PlayerResultPanel(PlayerResult result, string language, PanelMetrics m)
    {
        _result = result;
        Width = m.Width;
        Orientation = m.Vertical ? Orientation.Vertical : Orientation.Horizontal;
        RenderTransform = _slide;

        var accent = Frozen(result.Accent.R, result.Accent.G, result.Accent.B);
        var highlighted = result.IsWinner || result.IsTie;
        var border = highlighted ? Gold : accent;
        var cardWidth = m.Vertical ? m.Width : m.Width - m.ZoneWidth;

        // personaje: sin marco, en su zona, con los pies sobre una sombra pixelada
        _character = new PixelCharacter
        {
            Width = m.ZoneWidth, Height = m.ZoneHeight, Accent = accent, Source = result.Avatar,
            VisualScale = result.Tuning?.VisualScale ?? 1.0, GroundOffset = result.Tuning?.GroundOffset ?? 0.0,
            Overflow = result.Tuning?.Overflow ?? CharacterPlacement.DefaultOverflow,
            VerticalAlignment = VerticalAlignment.Bottom, RenderTransform = _characterSlide,
        };
        var zone = new Grid { Width = m.ZoneWidth, Children = { _character } };
        Panel.SetZIndex(zone, 1);

        if (highlighted)
        {
            _tag = new PixelFrame
            {
                Fill = Gold, BorderBrush = Frozen(0xFF, 0xF6, 0xC8), Shadow = Ink, NotchUnits = 2,
                Padding = new Thickness(12, 0, 12, 2), VerticalAlignment = VerticalAlignment.Top,
                Child = new PixelText
                {
                    Text = result.IsTie ? ResultTexts.Tie(language) : ResultTexts.Winner(language),
                    FontFamily = Pixel, FontSize = Math.Max(26, m.Name * 0.6), Foreground = Dark,
                },
            };
        }

        var name = new PixelText
        {
            Text = result.Name ?? ResultTexts.Player(language, result.Number), FontFamily = Pixel, FontSize = m.Name,
            Foreground = accent, Outline = Ink, OutlineSize = 3, MaxWidth = cardWidth - (highlighted ? 210 : 40),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var nameRow = new DockPanel { LastChildFill = true };
        if (_tag is not null)
        {
            DockPanel.SetDock(_tag, Dock.Right);
            nameRow.Children.Add(_tag);
        }

        nameRow.Children.Add(name);

        _scoreText = new PixelText
        {
            Text = result.Score.ToString("N0"), FontFamily = Pixel, FontSize = m.Score, Foreground = Text,
            Shadow = Frozen(0x2A, 0x2F, 0x66), ShadowOffset = Math.Max(4, m.Score / 22),
        };
        var points = new PixelText
        {
            Text = ResultTexts.Points(language), FontFamily = Pixel, FontSize = m.Points, Foreground = Dim,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 10),
        };
        var scoreRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { _scoreText, points } };

        var style = RankPalette.For(result.Rank);
        _stamp = new PixelFrame
        {
            Fill = Brush(style.Fill), BorderBrush = Brush(style.Border), Shadow = Ink, NotchUnits = 3, BorderUnits = 1,
            Padding = new Thickness(18, 0, 18, 2), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(6, 4, 0, 4),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = [_stampScale, new RotateTransform(-4)] },
            Child = new PixelText
            {
                Text = ResultRanks.Label(result.Rank, language), FontFamily = Pixel, FontSize = m.Stamp,
                Foreground = Brush(style.Text), Shadow = Ink, ShadowOffset = 3,
            },
        };

        // comentario: Jersey 15, sin sombra ni contorno, con interlineado cómodo
        var comment = new TextBlock
        {
            Text = $"“{result.Comment}”", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left,
            FontFamily = Pixel, FontSize = m.Comment, Foreground = Text,
            LineHeight = m.Comment * 1.12, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(4, 12, 4, 0),
        };
        _comment = comment;

        var card = new PixelFrame
        {
            Fill = PanelFill, BorderBrush = border, Shadow = Ink, Highlight = Frozen(0x33, 0xFF, 0xFF), NotchUnits = 3,
            BorderUnits = 1, Padding = new Thickness(18, 12, 18, 16), Width = cardWidth - 8,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new StackPanel { Children = { nameRow, scoreRow, _stamp, comment } },
        };

        Children.Add(zone);
        Children.Add(card);
    }

    private static Brush Brush(Color c) => Frozen(c.R, c.G, c.B);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ---- Animación ----------------------------------------------------------------------------------------

    /// <summary>
    /// Entrada: el personaje entra deslizándose desde la izquierda con pasos duros, el panel sube, el puntaje
    /// cuenta, el sello cae y después aparece el comentario. Todo con pasos discretos.
    /// </summary>
    public void Play(TimeSpan delay)
    {
        Stop();
        Opacity = 0;
        ShownScore = 0;
        _stamp.Opacity = 0;
        _comment.Opacity = 0;
        _character.Opacity = 0;

        BeginAnimation(OpacityProperty, Steps(delay, (0, 1)));
        _slide.BeginAnimation(TranslateTransform.YProperty, Steps(delay, (0, 48), (0.05, 36), (0.10, 24), (0.15, 12), (0.20, 4), (0.25, 0)));
        _character.BeginAnimation(OpacityProperty, Steps(delay, (0, 1)));
        _characterSlide.BeginAnimation(TranslateTransform.XProperty, Steps(delay, (0, -96), (0.05, -64), (0.10, -36), (0.15, -16), (0.20, -4), (0.25, 0)));

        var countStart = delay + TimeSpan.FromSeconds(0.35);
        var countDuration = TimeSpan.FromSeconds(0.5 + 1.3 * Math.Clamp(_result.Score / 10000.0, 0, 1));
        BeginAnimation(ShownScoreProperty, new DoubleAnimation(0, _result.Score, countDuration)
        {
            BeginTime = countStart,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        });

        var land = countStart + countDuration + TimeSpan.FromSeconds(0.15);
        _stamp.BeginAnimation(OpacityProperty, Steps(land, (0, 1)));
        var scale = Steps(land, (0, 3.0), (0.06, 2.2), (0.12, 1.5), (0.18, 1.15), (0.24, 1.0));
        _stampScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        _stampScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale.Clone());
        _comment.BeginAnimation(OpacityProperty, Steps(land + TimeSpan.FromSeconds(0.45), (0, 1)));

        if (_tag is not null)
        {
            _tag.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                BeginTime = land + TimeSpan.FromSeconds(0.7),
                Duration = TimeSpan.FromSeconds(0.9),
                RepeatBehavior = RepeatBehavior.Forever,
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new DiscreteDoubleKeyFrame(0.55, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.45))),
                },
            });
        }

        IsAnimating = true;
        _doneTimer = new DispatcherTimer { Interval = land + TimeSpan.FromSeconds(0.45 + 0.1) };
        _doneTimer.Tick += (_, _) =>
        {
            _doneTimer?.Stop();
            IsAnimating = false;
        };
        _doneTimer.Start();

        _landTimer = new DispatcherTimer { Interval = land };
        _landTimer.Tick += (_, _) =>
        {
            _landTimer?.Stop();
            StampLanded?.Invoke(this, EventArgs.Empty);
        };
        _landTimer.Start();
    }

    /// <summary>Estado final inmediato, sin animar (repintado, cambio de idioma o saltear).</summary>
    public void ShowFinal()
    {
        Stop();
        Opacity = 1;
        _slide.Y = 0;
        _characterSlide.X = 0;
        _character.Opacity = 1;
        ShownScore = _result.Score;
        _scoreText.Text = _result.Score.ToString("N0");
        _stamp.Opacity = 1;
        _stampScale.ScaleX = _stampScale.ScaleY = 1;
        _comment.Opacity = 1;
        if (_tag is not null)
        {
            _tag.Opacity = 1;
        }
    }

    public void Stop()
    {
        _landTimer?.Stop();
        _landTimer = null;
        _doneTimer?.Stop();
        _doneTimer = null;
        IsAnimating = false;
        BeginAnimation(OpacityProperty, null);
        BeginAnimation(ShownScoreProperty, null);
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        _characterSlide.BeginAnimation(TranslateTransform.XProperty, null);
        _character.BeginAnimation(OpacityProperty, null);
        _stamp.BeginAnimation(OpacityProperty, null);
        _stampScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _stampScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _comment.BeginAnimation(OpacityProperty, null);
        _tag?.BeginAnimation(OpacityProperty, null);
    }

    /// <summary>Animación de valores discretos: (segundos desde el inicio, valor).</summary>
    private static DoubleAnimationUsingKeyFrames Steps(TimeSpan begin, params (double Seconds, double Value)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = begin,
            FillBehavior = FillBehavior.HoldEnd,
        };
        foreach (var (seconds, value) in frames)
        {
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds))));
        }

        return animation;
    }
}
