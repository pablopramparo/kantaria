// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Kantor.App.Pixel;

/// <summary>
/// Select con el estilo del tema: muestra el valor elegido con una flecha y, al tocarlo (clic, Enter o Espacio),
/// despliega la lista de opciones. Arriba/abajo recorren la lista, Enter elige y Esc la cierra. Con un solo valor
/// no despliega nada.
/// </summary>
public sealed class ChoiceButton : Button
{
    private static readonly ControlTemplate ItemTemplate = (ControlTemplate)XamlReader.Parse(
        """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
            <Border x:Name="Bg" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="Left" VerticalAlignment="Center" />
            </Border>
            <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Bg" Property="Background" Value="#2A3370" />
                </Trigger>
                <Trigger Property="IsFocused" Value="True">
                    <Setter TargetName="Bg" Property="Background" Value="#3A4380" />
                </Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    private static int _openPopups;

    private readonly TextBlock _text = new() { HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Polygon _arrow = new()
    {
        Points = [new Point(0, 0), new Point(12, 0), new Point(6, 7)],
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 0, 4, 0),
    };

    private IReadOnlyList<string> _items = [];
    private int _index;
    private Popup? _popup;

    public ChoiceButton()
    {
        SetResourceReference(StyleProperty, "PixelButton");
        FontSize = 22;
        _arrow.SetResourceReference(Shape.FillProperty, "GoldBrush");
        var grid = new Grid();
        grid.Children.Add(_text);
        grid.Children.Add(_arrow);
        Content = grid;
    }

    /// <summary>Hay una lista desplegada: las pantallas no deben tomar Esc ni las flechas como propias.</summary>
    public static bool AnyOpen => _openPopups > 0;

    /// <summary>El valor elegido cambió por una acción del usuario (no al fijarlo desde código).</summary>
    public event EventHandler? Changed;

    public int SelectedIndex => _index;

    /// <summary>Define los valores y el elegido, sin avisar del cambio.</summary>
    public void Set(IReadOnlyList<string> items, int index)
    {
        _items = items;
        _index = items.Count == 0 ? -1 : Math.Clamp(index, 0, items.Count - 1);
        Render();
    }

    public void Select(int index)
    {
        if (_items.Count > 0)
        {
            _index = Math.Clamp(index, 0, _items.Count - 1);
            Render();
        }
    }

    protected override void OnClick()
    {
        base.OnClick();
        Open();
    }

    private void Render()
    {
        _text.Text = _index >= 0 && _index < _items.Count ? _items[_index] : string.Empty;
        _arrow.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _text.Margin = new Thickness(0, 0, _items.Count > 1 ? 18 : 0, 0); // deja lugar a la flecha
    }

    private void Open()
    {
        if (_items.Count < 2 || _popup is { IsOpen: true })
        {
            return;
        }

        // Los popups no heredan la escala de la pantalla (se arman sobre un Viewbox): se les aplica la misma.
        var scale = 1.0;
        if (Window.GetWindow(this) is { } window)
        {
            var transform = TransformToAncestor(window);
            scale = (transform.Transform(new Point(1, 0)) - transform.Transform(new Point(0, 0))).Length;
            if (scale <= 0.01)
            {
                scale = 1.0;
            }
        }

        var list = new StackPanel();
        var buttons = new List<Button>();
        for (var i = 0; i < _items.Count; i++)
        {
            var index = i;
            var item = new Button
            {
                Content = _items[i],
                Template = ItemTemplate,
                FontFamily = FontFamily,
                FontSize = Math.Max(16, FontSize),
                Foreground = i == _index ? (Brush)FindResource("GoldBrush") : (Brush)FindResource("TextBrush"),
                Background = Brushes.Transparent,
                Padding = new Thickness(14, 8, 14, 8),
                FocusVisualStyle = null,
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            item.Click += (_, _) =>
            {
                Close();
                if (index != _index)
                {
                    _index = index;
                    Render();
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            };
            buttons.Add(item);
            list.Children.Add(item);
        }

        var scroll = new ScrollViewer
        {
            Content = list,
            MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        scroll.Resources.Add(typeof(ScrollBar), (Style)FindResource("PixelScrollBar"));

        var frame = new Border
        {
            Child = scroll,
            Background = (Brush)FindResource("InkBrush"),
            BorderBrush = (Brush)FindResource("GoldBrush"),
            BorderThickness = new Thickness(2),
            MinWidth = Math.Max(ActualWidth, 200),
            LayoutTransform = new ScaleTransform(scale, scale),
        };
        frame.PreviewKeyDown += (_, e) =>
        {
            var focused = buttons.FindIndex(b => b.IsKeyboardFocused);
            switch (e.Key)
            {
                case Key.Down:
                    buttons[Math.Min(focused + 1, buttons.Count - 1)].Focus();
                    e.Handled = true;
                    break;
                case Key.Up:
                    buttons[Math.Max(focused - 1, 0)].Focus();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
            }
        };

        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            Child = frame,
        };
        _popup.Closed += OnPopupClosed;
        _openPopups++;
        _popup.IsOpen = true;
        Dispatcher.BeginInvoke(() => buttons[Math.Clamp(_index, 0, buttons.Count - 1)].Focus(),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Close()
    {
        if (_popup is { IsOpen: true })
        {
            _popup.IsOpen = false;
        }
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        if (_popup is not null)
        {
            _popup.Closed -= OnPopupClosed;
            _popup = null;
            _openPopups = Math.Max(0, _openPopups - 1);
        }

        Focus();
    }
}

/// <summary>Medidor de nivel en bloques: verde, dorado cerca del tope y rojo al saturar.</summary>
public sealed class PixelMeter : FrameworkElement
{
    private const double Block = 8;
    private const double Gap = 2;

    private static readonly Brush Back = Frozen(Color.FromRgb(0x14, 0x1B, 0x4A));
    private static readonly Brush Green = Frozen(Color.FromRgb(0x5B, 0xE5, 0x6B));
    private static readonly Brush Gold = Frozen(Color.FromRgb(0xFF, 0xC9, 0x33));
    private static readonly Brush Red = Frozen(Color.FromRgb(0xFF, 0x4D, 0x4D));

    private double _value;

    public PixelMeter()
    {
        Height = 16;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    /// <summary>Nivel de 0 a 1.</summary>
    public double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(clamped - _value) > 0.004)
            {
                _value = clamped;
                InvalidateVisual();
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var count = Math.Max(1, (int)((ActualWidth + Gap) / (Block + Gap)));
        var lit = (int)Math.Round(_value * count);
        for (var i = 0; i < count; i++)
        {
            var fraction = (i + 1) / (double)count;
            var brush = i >= lit ? Back : fraction > 0.92 ? Red : fraction > 0.75 ? Gold : Green;
            dc.DrawRectangle(brush, null, new Rect(i * (Block + Gap), 0, Block, ActualHeight));
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
