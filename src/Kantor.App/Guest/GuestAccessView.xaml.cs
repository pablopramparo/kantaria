// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kantor.Core.Menu;

namespace Kantor.App.Guest;

/// <summary>
/// Pantalla "SUMATE AL KARAOKE": QR grande, dirección legible, estado y el interruptor del acceso. No tiene
/// controles de red: la elección de la interfaz vive en Opciones › Juego.
/// </summary>
public partial class GuestAccessView : UserControl
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x5B, 0xE5, 0x6B));

    public event EventHandler? BackRequested;

    public event EventHandler? ToggleRequested;

    public GuestAccessView()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                BackRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && !e.Handled && Keyboard.FocusedElement is not Button)
            {
                ToggleRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        };
    }

    public void Show(string language, GuestView view)
    {
        TitleText.Text = GuestTexts.Title(language);
        BackButton.Content = GuestTexts.Back(language);
        HintText.Text = GuestTexts.Hint(language);
        var on = view.Active && view.Qr is not null;
        Qr.Modules = view.Qr;
        Qr.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        QrOff.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        QrOff.Text = view.Message ?? GuestTexts.Inactive(language);
        StatusText.Text = on ? "● " + GuestTexts.Active(language) : "○ " + GuestTexts.Inactive(language);
        StatusText.Foreground = on ? Green : (Brush)FindResource("DimBrush");
        ScanText.Text = on ? GuestTexts.Scan(language) : GuestTexts.What(language);
        AddressText.Text = on && view.BaseUrl is not null ? GuestTexts.Address(language, view.BaseUrl) : string.Empty;
        AddressText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        NoteText.Text = on ? GuestTexts.SameWifi(language) + "\n" + GuestTexts.OnNote(language)
            : view.Message is not null ? GuestTexts.OffNote(language) : GuestTexts.OffNote(language);
        ToggleButton.Content = on ? GuestTexts.TurnOff(language) : GuestTexts.TurnOn(language);
        Dispatcher.BeginInvoke(() => ToggleButton.Focus(), DispatcherPriority.Input);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void OnToggleClick(object sender, RoutedEventArgs e) => ToggleRequested?.Invoke(this, EventArgs.Empty);
}
