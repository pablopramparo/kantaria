// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kantor.App.Pixel;
using Kantor.Core.Menu;

namespace Kantor.App.Guest;

/// <summary>Lo que se ve del acceso de invitados en cada momento.</summary>
public sealed record GuestView(bool Active, string? BaseUrl, bool[,]? Qr, string? Message);

/// <summary>
/// Tarjeta chica del acceso de invitados para el menú y la lista de canciones: apagado muestra un botón para
/// activarlo; activo muestra el QR (al tocarlo se abre la pantalla grande, donde también se apaga).
/// </summary>
public sealed class GuestCard : Border
{
    private readonly QrView _qr;
    private readonly TextBlock _title;
    private readonly TextBlock _caption;
    private readonly Button _turnOn;
    private readonly StackPanel _active;
    private readonly bool _compact;

    public event EventHandler? ActivateRequested;

    public event EventHandler? EnlargeRequested;

    public GuestCard(bool compact)
    {
        _compact = compact;
        var pixel = (FontFamily)Application.Current.FindResource("PixelFont");
        _qr = new QrView { Side = compact ? 104 : 176, IntegerCells = !compact, Cursor = Cursors.Hand };
        _qr.MouseLeftButtonUp += (_, _) => EnlargeRequested?.Invoke(this, EventArgs.Empty);

        _title = new TextBlock
        {
            FontFamily = pixel, FontSize = compact ? 20 : 26, Foreground = (Brush)Application.Current.FindResource("GoldBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = compact ? 150 : 176,
        };
        _caption = new TextBlock
        {
            FontFamily = pixel, FontSize = compact ? 18 : 22, Foreground = (Brush)Application.Current.FindResource("DimBrush"),
            TextWrapping = TextWrapping.Wrap, MaxWidth = compact ? 150 : 176,
        };

        var frame = new PixelFrame
        {
            Fill = (Brush)Application.Current.FindResource("PanelBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("P1Brush"),
            Shadow = (Brush)Application.Current.FindResource("InkBrush"),
            Highlight = (Brush)Application.Current.FindResource("HighlightBrush"),
            BorderUnits = 1, NotchUnits = 2, ShadowUnits = 1, Padding = new Thickness(compact ? 4 : 8),
        };

        var info = new StackPanel { Margin = compact ? new Thickness(8, 0, 0, 0) : new Thickness(0, 8, 0, 0) };
        info.Children.Add(_title);
        info.Children.Add(_caption);
        _active = new StackPanel { Orientation = compact ? Orientation.Horizontal : Orientation.Vertical };
        _active.Children.Add(_qr);
        _active.Children.Add(info);

        _turnOn = new Button
        {
            Style = (Style)Application.Current.FindResource("PixelButton"), FontSize = compact ? 22 : 26, Focusable = false,
        };
        _turnOn.Click += (_, _) => ActivateRequested?.Invoke(this, EventArgs.Empty);

        var host = new Grid();
        host.Children.Add(_active);
        host.Children.Add(_turnOn);
        frame.Child = host;
        Child = frame;
        Visibility = Visibility.Collapsed;
    }

    public void Update(string language, GuestView view)
    {
        _title.Text = GuestTexts.CardOn(language);
        _caption.Text = GuestTexts.CardScan(language);
        _turnOn.Content = _compact
            ? GuestTexts.CardTitle(language) + " · QR"
            : GuestTexts.CardTitle(language) + " · QR";
        _qr.ToolTip = GuestTexts.CardEnlarge(language);
        var showQr = view.Active && view.Qr is not null;
        _qr.Modules = view.Qr;
        _active.Visibility = showQr ? Visibility.Visible : Visibility.Collapsed;
        _turnOn.Visibility = showQr ? Visibility.Collapsed : Visibility.Visible;
        Visibility = Visibility.Visible;
    }
}
