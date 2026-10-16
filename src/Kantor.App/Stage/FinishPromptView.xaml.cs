// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kantor.Core.Menu;

namespace Kantor.App.Stage;

/// <summary>
/// "¿Terminar la canción?": se muestra al salir antes de que se acabe la letra. Ofrece ver los resultados guardándolos,
/// verlos sin guardar o seguir cantando. Escape equivale a seguir cantando. No decide nada por sí sola: avisa.
/// </summary>
public partial class FinishPromptView : UserControl
{
    private Button[] _buttons = [];

    public event EventHandler? SaveRequested;

    public event EventHandler? DiscardRequested;

    public event EventHandler? ContinueRequested;

    public FinishPromptView()
    {
        InitializeComponent();
        _buttons = [SaveButton, DiscardButton, ContinueButton];
        PreviewKeyDown += OnKeyDown;
    }

    /// <param name="canSave">La interpretación se guardaría (hay partida abierta); si no, solo se ofrece ver o seguir.</param>
    public void Show(string language, bool canSave)
    {
        TitleText.Text = MenuTexts.FinishTitle(language);
        InfoText.Text = MenuTexts.FinishInfo(language, canSave);
        SaveButton.Content = MenuTexts.FinishSave(language);
        DiscardButton.Content = canSave ? MenuTexts.FinishDiscard(language) : MenuTexts.FinishView(language);
        ContinueButton.Content = MenuTexts.FinishContinue(language);
        SaveButton.Visibility = canSave ? Visibility.Visible : Visibility.Collapsed;
        _buttons = canSave ? [SaveButton, DiscardButton, ContinueButton] : [DiscardButton, ContinueButton];
        Dispatcher.BeginInvoke(() => _buttons[0].Focus(), DispatcherPriority.Input);
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);

    private void OnDiscard(object sender, RoutedEventArgs e) => DiscardRequested?.Invoke(this, EventArgs.Empty);

    private void OnContinue(object sender, RoutedEventArgs e) => ContinueRequested?.Invoke(this, EventArgs.Empty);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                ContinueRequested?.Invoke(this, EventArgs.Empty);
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
