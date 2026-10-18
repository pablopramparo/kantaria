// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kantor.Core.Menu;

namespace Kantor.App.Stage;

/// <summary>
/// Menú de pausa del escenario (Escape durante el canto): seguir, terminar o salir. Se muestra sobre el escenario, sin
/// salir de pantalla completa. Escape equivale a seguir. No decide nada por sí sola: avisa.
/// </summary>
public partial class PauseMenuView : UserControl
{
    private readonly Button[] _buttons;

    public event EventHandler? ResumeRequested;

    public event EventHandler? FinishRequested;

    public event EventHandler? ExitRequested;

    public PauseMenuView()
    {
        InitializeComponent();
        _buttons = [ResumeButton, FinishButton, ExitButton];
        PreviewKeyDown += OnKeyDown;
    }

    public void Show(string language)
    {
        TitleText.Text = MenuTexts.Pause(language);
        ResumeButton.Content = MenuTexts.Resume(language);
        FinishButton.Content = MenuTexts.Finish(language);
        ExitButton.Content = MenuTexts.Exit(language);
        Dispatcher.BeginInvoke(() => ResumeButton.Focus(), DispatcherPriority.Input);
    }

    private void OnResume(object sender, RoutedEventArgs e) => ResumeRequested?.Invoke(this, EventArgs.Empty);

    private void OnFinish(object sender, RoutedEventArgs e) => FinishRequested?.Invoke(this, EventArgs.Empty);

    private void OnExit(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                ResumeRequested?.Invoke(this, EventArgs.Empty);
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
