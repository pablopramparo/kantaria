// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Kantor.App.Pixel;

/// <summary>Piezas de interfaz con el estilo del tema, para pantallas que se arman por código.</summary>
public static class PixelUi
{
    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    public static Brush Brush(string key) => Res<Brush>(key);

    public static FontFamily Pixel => Res<FontFamily>("PixelFont");

    public static FontFamily Body => Res<FontFamily>("BodyFont");

    /// <summary>Renglón: etiqueta pixel a la izquierda y el control a la derecha.</summary>
    public static Grid Row(string label, FrameworkElement control, double controlWidth = 440, double labelWidth = 400)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = label,
            FontFamily = Pixel,
            FontSize = 28,
            Foreground = Brush("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        control.Width = controlWidth;
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    public static ChoiceButton Choice(string key, IReadOnlyList<string> items, int index, Action<int> onChanged,
        Action<string>? onFocus = null)
    {
        var choice = new ChoiceButton { Tag = key };
        choice.Set(items, index);
        if (onFocus is not null)
        {
            choice.GotKeyboardFocus += (_, _) => onFocus(key);
        }

        choice.Changed += (_, _) => onChanged(choice.SelectedIndex);
        return choice;
    }

    public static Button Action(string key, string text, Action onClick, double fontSize = 20, Action<string>? onFocus = null)
    {
        var button = new Button { Content = text, FontSize = fontSize, Tag = key };
        button.SetResourceReference(FrameworkElement.StyleProperty, "PixelButton");
        if (onFocus is not null)
        {
            button.GotKeyboardFocus += (_, _) => onFocus(key);
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    public static TextBlock Note(string text, double fontSize = 15) => new()
    {
        Text = text,
        FontFamily = Body,
        FontSize = fontSize,
        Foreground = Brush("DimBrush"),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 1060,
        Margin = new Thickness(0, 4, 0, 4),
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontFamily = Pixel,
        FontSize = 32,
        Foreground = Brush("GoldBrush"),
        Margin = new Thickness(0, 18, 0, 6),
    };

    /// <summary>
    /// Retrato cuadrado de un jugador: su imagen o, sin ella, una silueta de cabeza y hombros del color del puesto
    /// (apagada si <paramref name="empty"/>: nadie asignado).
    /// </summary>
    public static FrameworkElement Avatar(ImageSource? image, Color color, double size, bool empty = false)
    {
        var frame = new Border
        {
            Width = size,
            Height = size,
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x10, 0x40)),
            BorderBrush = new SolidColorBrush(empty ? Color.FromArgb(0x80, color.R, color.G, color.B) : color),
            BorderThickness = new Thickness(2),
            SnapsToDevicePixels = true,
        };

        if (image is not null)
        {
            var picture = new Image { Source = image, Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor); // pixel art: sin suavizado
            frame.Child = picture;
            return frame;
        }

        var u = (size - 4) / 8;
        var tint = new SolidColorBrush(empty ? Color.FromArgb(0x55, color.R, color.G, color.B) : color);
        var canvas = new Canvas { Width = size - 4, Height = size - 4 };
        void Block(double x, double y, double w, double h)
        {
            var rect = new System.Windows.Shapes.Rectangle { Width = w * u, Height = h * u, Fill = tint };
            Canvas.SetLeft(rect, x * u);
            Canvas.SetTop(rect, y * u);
            canvas.Children.Add(rect);
        }

        Block(2.5, 1, 3, 3); // cabeza
        Block(1, 5, 6, 3);   // hombros
        frame.Child = canvas;
        return frame;
    }

    /// <summary>Campo de texto de una línea con el aspecto del tema.</summary>
    public static TextBox Input(string key, double width = 420, double fontSize = 26)
    {
        var box = new TextBox
        {
            Tag = key,
            Width = width,
            FontFamily = Pixel,
            FontSize = fontSize,
            Foreground = Brush("TextBrush"),
            Background = Brush("InkBrush"),
            BorderBrush = Brush("P1Brush"),
            BorderThickness = new Thickness(2),
            CaretBrush = Brush("GoldBrush"),
            Padding = new Thickness(8, 4, 8, 4),
            MaxLength = 24,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        // Un campo de una línea se queda con las flechas para mover el cursor y no deja salir el foco: con el
        // teclado hay que poder pasar a los controles de arriba, abajo y a los costados. ↑ ↓ siempre salen;
        // ← → salen solo cuando el cursor ya está en el borde del texto.
        box.PreviewKeyDown += (_, e) =>
        {
            FocusNavigationDirection? direction = e.Key switch
            {
                Key.Up => FocusNavigationDirection.Up,
                Key.Down => FocusNavigationDirection.Down,
                Key.Left when box.SelectionLength == 0 && box.CaretIndex == 0 => FocusNavigationDirection.Left,
                Key.Right when box.SelectionLength == 0 && box.CaretIndex == box.Text.Length =>
                    FocusNavigationDirection.Right,
                _ => null,
            };

            if (direction is { } d)
            {
                box.MoveFocus(new TraversalRequest(d));
                e.Handled = true;
            }
        };

        return box;
    }
}
