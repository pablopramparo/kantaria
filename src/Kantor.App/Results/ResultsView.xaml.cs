// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Kantor.App.Pixel;
using Kantor.Core.Menu;
using Kantor.Core.Results;

namespace Kantor.App.Results;

/// <summary>
/// Pantalla de resultados de 1 a 4 jugadores; solo muestra lo que recibe (no conoce puntaje ni comentarios).
/// Con un jugador: composición centrada. Con 2 a 4: clasificación permanente a la izquierda y presentación
/// individual a la derecha, con una ceremonia automática (ganador primero, luego los demás de mayor a menor y,
/// al final, el ganador otra vez con la pantalla estable).
/// </summary>
public partial class ResultsView : UserControl
{
    private static readonly Brush Ink = Frozen(0x05, 0x07, 0x18);
    private static readonly Brush RowFill = Frozen(0x10, 0x14, 0x2E);
    private static readonly Brush RowCurrentFill = Frozen(0x23, 0x2A, 0x5E);
    private static readonly Brush Gold = Frozen(0xFF, 0xC9, 0x33);
    private static readonly Brush Dark = Frozen(0x2A, 0x15, 0x00);
    private static readonly Brush White = Frozen(0xF4, 0xF6, 0xFF);

    /// <summary>Segundos entre la entrada de un jugador y la del siguiente (la animación de uno dura unos 3 s).</summary>
    private const double StaggerSeconds = 2.4;

    private readonly List<PlayerResultPanel> _panels = [];
    private readonly ResultsInputGate _gate = new();
    private IReadOnlyList<PlayerResult> _players = [];
    private int _columns = 1;

    /// <summary>"Otra vez" fue elegido.</summary>
    public event EventHandler? ReplayRequested;

    /// <summary>"Siguiente" (la primera canción de la cola) fue elegido.</summary>
    public event EventHandler? NextRequested;

    /// <summary>Muestra u oculta el botón "Siguiente" con el título de la primera canción de la cola.</summary>
    public void SetNext(string? title, string language)
    {
        NextButton.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        if (title is not null)
        {
            NextButton.Content = QueueTexts.Next(language, title);
        }
    }

    private void OnNextClick(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Muestra (o quita, con <c>null</c>) la cuenta regresiva del avance automático.</summary>
    public void SetCountdown(string? text)
    {
        CountdownText.Text = text ?? string.Empty;
        CountdownText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>"Cerrar" fue elegido (o Escape).</summary>
    public event EventHandler? CloseRequested;

    public ResultsView()
    {
        InitializeComponent();
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
    }

    /// <summary>
    /// Muestra los resultados: todos los jugadores a la vez (2 en columnas, 3 en fila, 4 en grilla de 2×2). Con
    /// <paramref name="animate"/> entran de a uno, del último puesto al primero; sin ella (cambio de idioma o
    /// repintado) quedan directamente en su estado final.
    /// </summary>
    public void Show(IReadOnlyList<PlayerResult> players, string language, bool animate)
    {
        StopAnimations();
        foreach (var panel in _panels)
        {
            panel.StampLanded -= OnStampLanded;
        }

        _panels.Clear();
        Columns.Children.Clear();
        _gate.Reset();

        TitleText.Text = ResultTexts.Title(language);
        ReplayButton.Content = ResultTexts.Replay(language);
        CloseButton.Content = ResultTexts.Close(language);

        var count = Math.Clamp(players.Count, 1, 4);
        _players = players.Take(count).ToList();
        Backdrop.Accents = _players.Select(p => p.Accent).ToArray();
        var metrics = PanelMetrics.For(count);
        foreach (var player in _players)
        {
            var panel = new PlayerResultPanel(player, language, metrics);
            panel.StampLanded += OnStampLanded;
            _panels.Add(panel);
        }

        _columns = count == 4 ? 2 : count;
        var layout = new UniformGrid
        {
            Columns = _columns, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        };
        foreach (var panel in _panels)
        {
            panel.Margin = new Thickness(count == 1 ? 0 : 6, 0, count == 1 ? 0 : 6, count == 4 ? 12 : 0);
            layout.Children.Add(panel);
        }

        Columns.Children.Add(layout);

        if (animate)
        {
            // de abajo hacia arriba: el último puesto entra primero y el ganador cierra
            var entrance = Enumerable.Range(0, count).OrderBy(i => _players[i].Score).ThenByDescending(i => i).ToArray();
            for (var step = 0; step < entrance.Length; step++)
            {
                _panels[entrance[step]].Play(TimeSpan.FromSeconds(StaggerSeconds * step));
            }
        }
        else
        {
            foreach (var panel in _panels)
            {
                panel.ShowFinal();
            }
        }

        Dispatcher.BeginInvoke(() => (NextButton.IsVisible ? NextButton : ReplayButton).Focus(), DispatcherPriority.Input);
    }

    // ---- Entrada ------------------------------------------------------------------------------------------

    public bool IsAnimating => _panels.Any(p => p.IsAnimating);

    /// <summary>Completa de inmediato las animaciones en curso: todos quedan en su estado final.</summary>
    public void SkipAnimation()
    {
        foreach (var panel in _panels)
        {
            panel.ShowFinal();
        }
    }

    public void StopAnimations()
    {
        foreach (var panel in _panels)
        {
            panel.Stop();
        }
    }

    private void OnStampLanded(object? sender, EventArgs e)
    {
        var panel = (PlayerResultPanel)sender!;
        var index = Math.Max(0, _panels.IndexOf(panel));
        var x = (index % _columns + 0.5) / _columns;
        var y = _panels.Count == 4 ? (index < 2 ? 0.35 : 0.65) : 0.42;
        var count = panel.Rank switch
        {
            ResultRank.Perfect => 150,
            ResultRank.Legendary => 110,
            ResultRank.Superstar => 80,
            ResultRank.Impressive => 55,
            ResultRank.NiceWork => 40,
            _ => 24,
        };
        Backdrop.Burst(x, y, count);
    }

    private void OnReplayClick(object sender, RoutedEventArgs e) => ReplayRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private static ResultsKey Map(Key key) => key switch
    {
        Key.Enter => ResultsKey.Enter,
        Key.Space => ResultsKey.Space,
        Key.Escape => ResultsKey.Escape,
        Key.Left => ResultsKey.Left,
        Key.Right => ResultsKey.Right,
        Key.End => ResultsKey.End,
        _ => ResultsKey.Other,
    };

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = Map(e.Key);
        switch (_gate.KeyDown(key, IsAnimating))
        {
            case ResultsKeyAction.Skip:
                SkipAnimation();
                e.Handled = true;
                return;
            case ResultsKeyAction.Swallow:
                e.Handled = true;
                return;
            case ResultsKeyAction.Close:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
        }

        if (key is ResultsKey.Left or ResultsKey.Right)
        {
            // navegación entre los botones visibles con el teclado
            var buttons = new[] { NextButton, ReplayButton, CloseButton }.Where(b => b.Visibility == Visibility.Visible).ToList();
            var at = Math.Max(0, buttons.FindIndex(b => ReferenceEquals(Keyboard.FocusedElement, b)));
            var step = key == ResultsKey.Right ? 1 : buttons.Count - 1;
            buttons[(at + step) % buttons.Count].Focus();
            e.Handled = true;
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (_gate.KeyUp(Map(e.Key)))
        {
            e.Handled = true; // la liberación de la pulsación que saltó o avanzó no activa nada
        }
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
