// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kantor.Core.Menu;

namespace Kantor.App.Players;

/// <summary>
/// Puerta de "Cantar": cómo se va a cantar. Con una sesión abierta se ofrece continuarla, iniciar una nueva (que
/// cierra la anterior) o cantar sin sesión; sin sesión abierta, iniciar una o cantar sin sesión. No cierra nada
/// por sí sola. Escape cancela y vuelve sin decidir.
/// </summary>
public partial class SessionPromptView : UserControl
{
    private Button[] _buttons = [];

    public event EventHandler? ContinueRequested;

    public event EventHandler? NewRequested;

    public event EventHandler? FreeRequested;

    public event EventHandler? CancelRequested;

    public SessionPromptView()
    {
        InitializeComponent();
        PreviewKeyDown += OnKeyDown;
    }

    /// <param name="hasOpenSession">Hay una sesión abierta que se puede continuar.</param>
    /// <param name="startedText">Fecha de inicio ya formateada (solo con sesión abierta).</param>
    /// <param name="songs">Canciones terminadas en la sesión.</param>
    /// <param name="players">Nombres de los jugadores de la sesión.</param>
    public void Show(string language, bool hasOpenSession, string? sessionName, string startedText, int songs,
        IReadOnlyList<string> players)
    {
        if (hasOpenSession)
        {
            TitleText.Text = PlayersTexts.Get(language, "prompt.title");
            var name = string.IsNullOrWhiteSpace(sessionName) ? string.Empty : $"“{sessionName}” · ";
            InfoText.Text = name + PlayersTexts.Format(language, "prompt.info", startedText,
                PlayersTexts.Songs(language, songs));
            PlayersText.Text = string.Join("  ·  ", players);
            ContinueButton.Content = PlayersTexts.Get(language, "prompt.continue");
            NewButton.Content = PlayersTexts.Get(language, "prompt.new");
            HintText.Text = PlayersTexts.Get(language, "prompt.hint") + "\n" + PlayersTexts.Get(language, "gate.hint");
        }
        else
        {
            TitleText.Text = PlayersTexts.Get(language, "gate.none");
            InfoText.Text = string.Empty;
            PlayersText.Text = string.Empty;
            NewButton.Content = PlayersTexts.Get(language, "gate.start");
            HintText.Text = PlayersTexts.Get(language, "gate.hint");
        }

        FreeButton.Content = PlayersTexts.Get(language, "gate.free");
        BackButton.Content = PlayersTexts.Get(language, "back");
        ContinueButton.Visibility = hasOpenSession ? Visibility.Visible : Visibility.Collapsed;
        InfoText.Visibility = PlayersText.Visibility = hasOpenSession ? Visibility.Visible : Visibility.Collapsed;
        _buttons = hasOpenSession
            ? [ContinueButton, NewButton, FreeButton, BackButton]
            : [NewButton, FreeButton, BackButton];
        Dispatcher.BeginInvoke(() => _buttons[0].Focus(), DispatcherPriority.Input);
    }

    private void OnContinue(object sender, RoutedEventArgs e) => ContinueRequested?.Invoke(this, EventArgs.Empty);

    private void OnNew(object sender, RoutedEventArgs e) => NewRequested?.Invoke(this, EventArgs.Empty);

    private void OnFree(object sender, RoutedEventArgs e) => FreeRequested?.Invoke(this, EventArgs.Empty);

    private void OnBack(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CancelRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
            case Key.Up or Key.Down:
                var index = Array.IndexOf(_buttons, Keyboard.FocusedElement);
                var next = e.Key == Key.Down ? (index + 1) % _buttons.Length : (index <= 0 ? _buttons.Length - 1 : index - 1);
                _buttons[next].Focus();
                e.Handled = true;
                break;
        }
    }
}
