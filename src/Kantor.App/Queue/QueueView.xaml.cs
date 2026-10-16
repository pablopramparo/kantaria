// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kantor.App.Pixel;
using Kantor.Core.Data;
using Kantor.Core.Menu;

namespace Kantor.App.Queue;

/// <summary>
/// Cola de canciones: lista ordenada con subir, bajar, quitar y cantar ya. La pantalla no guarda nada: pide la
/// cola actual y avisa de cada cambio (la aplicación la persiste).
/// </summary>
public partial class QueueView : UserControl
{
    private Func<IReadOnlyList<QueueEntry>> _get = () => [];
    private Action<string, int> _move = (_, _) => { };
    private Action<string> _remove = _ => { };
    private string _language = "es";
    private bool _persistedWithSession;
    private string? _focusKey;

    /// <summary>Se pidió salir de la pantalla.</summary>
    public event EventHandler? BackRequested;

    /// <summary>Se pidió cantar ya el elemento con ese identificador (la aplicación lo marca en ejecución).</summary>
    public event EventHandler<string>? SingRequested;

    public QueueView()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                BackRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        };
    }

    /// <param name="get">Los pendientes, en orden.</param>
    /// <param name="move">Mueve un elemento (identificador, −1 sube, +1 baja).</param>
    /// <param name="remove">Quita un elemento (identificador).</param>
    public void Show(Func<IReadOnlyList<QueueEntry>> get, Action<string, int> move, Action<string> remove,
        string language, bool persistedWithSession)
    {
        _get = get;
        _move = move;
        _remove = remove;
        _language = language;
        _persistedWithSession = persistedWithSession;
        _focusKey = null;
        TitleText.Text = QueueTexts.Title(language);
        BackButton.Content = QueueTexts.Back(language);
        HintText.Text = QueueTexts.Hint(language);
        Build();
    }

    private void Build()
    {
        var queue = _get();
        ContentPanel.Children.Clear();
        ContentPanel.Children.Add(PixelUi.Note(QueueTexts.Scope(_language, _persistedWithSession), 18));

        if (queue.Count == 0)
        {
            ContentPanel.Children.Add(PixelUi.Note(QueueTexts.Empty(_language), 22));
        }

        for (var i = 0; i < queue.Count; i++)
        {
            var index = i;
            var item = queue[i];
            var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var number = new TextBlock
            {
                Text = $"{i + 1}.",
                FontFamily = PixelUi.Pixel,
                FontSize = 34,
                Foreground = PixelUi.Brush(i == 0 ? "GoldBrush" : "DimBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontFamily = PixelUi.Pixel,
                FontSize = 28,
                Foreground = PixelUi.Brush("TextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            label.Children.Add(new TextBlock
            {
                Text = item.Artist,
                FontFamily = PixelUi.Body,
                FontSize = 18,
                Foreground = PixelUi.Brush("DimBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var id = item.ItemId;
            buttons.Children.Add(PixelUi.Action($"sing{index}", QueueTexts.SingNow(_language),
                () => SingRequested?.Invoke(this, id), 18, key => _focusKey = key));
            AddButton(buttons, $"up{index}", QueueTexts.Up(_language), () => Change(() => _move(id, -1)));
            AddButton(buttons, $"down{index}", QueueTexts.Down(_language), () => Change(() => _move(id, +1)));
            AddButton(buttons, $"del{index}", QueueTexts.Remove(_language), () => Change(() => _remove(id)));

            Grid.SetColumn(label, 1);
            Grid.SetColumn(buttons, 2);
            row.Children.Add(number);
            row.Children.Add(label);
            row.Children.Add(buttons);
            ContentPanel.Children.Add(row);
        }

        Dispatcher.BeginInvoke(() =>
        {
            var target = _focusKey is not null ? FindByTag(ContentPanel, _focusKey) : null;
            (target ?? FindByTag(ContentPanel, "sing0") ?? (IInputElement)BackButton).Focus();
        }, DispatcherPriority.Loaded);
    }

    private void AddButton(StackPanel into, string key, string text, Action action)
    {
        var button = PixelUi.Action(key, text, action, 18, k => _focusKey = k);
        button.Margin = new Thickness(10, 0, 0, 0);
        into.Children.Add(button);
    }

    private void Change(Action action)
    {
        action();
        Build();
    }

    private static FrameworkElement? FindByTag(DependencyObject root, string key)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Tag: string tag } element && tag == key)
            {
                return element;
            }

            if (FindByTag(child, key) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}
