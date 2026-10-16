// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kantor.Core.Menu;

namespace Kantor.App.Menu;

/// <summary>
/// Pantalla de inicio: logo, tres opciones y fondo de escenario. No sabe qué hace cada opción: solo avisa.
/// Se navega con teclado (flechas y Enter) o con el mouse.
/// </summary>
public partial class MainMenuView : UserControl
{
    public event EventHandler? SingRequested;

    public event EventHandler? PlayersRequested;

    public event EventHandler? OptionsRequested;

    public event EventHandler? HelpRequested;

    public event EventHandler? ExitRequested;

    public MainMenuView()
    {
        InitializeComponent();
        PreviewKeyDown += OnKeyDown;

        // el logo "respira": sube y baja unos píxeles, en pasos duros
        var bob = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.2), RepeatBehavior = RepeatBehavior.Forever };
        bob.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        bob.KeyFrames.Add(new DiscreteDoubleKeyFrame(-6, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.6))));
        LogoBob.BeginAnimation(TranslateTransform.YProperty, bob);
    }

    /// <summary>Aplica el idioma y deja el foco en la primera opción.</summary>
    public void Show(string language)
    {
        SingButton.Content = MenuTexts.Sing(language);
        PlayersButton.Content = MenuTexts.Players(language);
        OptionsButton.Content = MenuTexts.Options(language);
        ExitButton.Content = MenuTexts.Exit(language);
        HintText.Text = MenuTexts.Hint(language);
        Dispatcher.BeginInvoke(() => SingButton.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Línea con la sesión activa (vacía si no hay).</summary>
    public void SetSession(string text) => SessionText.Text = text;

    private void OnSingClick(object sender, RoutedEventArgs e) => SingRequested?.Invoke(this, EventArgs.Empty);

    private void OnPlayersClick(object sender, RoutedEventArgs e) => PlayersRequested?.Invoke(this, EventArgs.Empty);

    private void OnOptionsClick(object sender, RoutedEventArgs e) => OptionsRequested?.Invoke(this, EventArgs.Empty);

    private void OnHelpClick(object sender, RoutedEventArgs e) => HelpRequested?.Invoke(this, EventArgs.Empty);

    private void OnExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down))
        {
            return;
        }

        Button[] buttons = [SingButton, PlayersButton, OptionsButton, ExitButton];
        var index = Array.IndexOf(buttons, Keyboard.FocusedElement);
        var next = e.Key == Key.Down ? (index + 1) % buttons.Length : (index <= 0 ? buttons.Length - 1 : index - 1);
        buttons[next].Focus();
        e.Handled = true;
    }
}
