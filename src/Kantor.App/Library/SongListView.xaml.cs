// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kantor.Core.Library;
using Kantor.Core.Menu;

namespace Kantor.App.Library;

/// <summary>Una celda de la grilla: la canción y si es la seleccionada.</summary>
public sealed class GridCell(SongEntry song) : INotifyPropertyChanged
{
    private bool _isSelected;

    public SongEntry Song { get; } = song;

    public int Index { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Una fila de la grilla (se virtualizan filas, no celdas).</summary>
public sealed record GridRow(IReadOnlyList<GridCell> Cells);

/// <summary>
/// Lista de canciones con buscador, en vista de lista (con detalle) o de grilla. El foco queda siempre en el
/// buscador (se escribe directo) y las flechas mueven la selección. No sabe cargar canciones: avisa con eventos.
/// </summary>
public partial class SongListView : UserControl
{
    /// <summary>Celdas por fila de la grilla (con el panel de detalle a la derecha).</summary>
    public const int GridColumns = 5;

    private static readonly CoverConverter BigCover = new() { DecodeWidth = 640 };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private IReadOnlyList<SongEntry> _all = [];
    private List<SongEntry> _results = [];
    private List<GridCell> _cells = [];
    private int _selected = -1;
    private bool _grid;
    private string _language = "es";
    private int _scanned = -1;

    public event EventHandler<SongEntry>? SongChosen;

    public event EventHandler? BackRequested;

    public event EventHandler? FoldersRequested;

    /// <summary>Se pidió agregar la canción resaltada a la cola.</summary>
    public event EventHandler<SongEntry>? QueueAddRequested;

    /// <summary>Se pidió abrir la cola.</summary>
    public event EventHandler? QueueRequested;

    /// <summary>Se pidió ver o editar la partida (jugadores y estadísticas).</summary>
    public event EventHandler? SessionRequested;

    /// <summary>El usuario cambió entre lista y grilla (para recordarlo).</summary>
    public event EventHandler? ViewModeChanged;

    public SongListView()
    {
        InitializeComponent();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ApplyFilter();
        };
        PreviewKeyDown += OnKeyDown;
        _videoTimer.Tick += (_, _) => PreviewVideo.Sync(_videoStart + _videoClock.Elapsed, true, _videoGap);
        PreviewVideo.FirstFrame += (_, _) =>
        {
            if (_videoActive)
            {
                PreviewVideo.Visibility = Visibility.Visible; // recién ahora hay algo que ver: hasta entonces, la carátula
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Dispatcher.BeginInvoke(() => SearchBox.Focus(), DispatcherPriority.Input);
            }
            else
            {
                HidePreviewVideo();
            }
        };
    }

    /// <summary><c>true</c> = grilla; <c>false</c> = lista con detalle.</summary>
    public bool GridMode
    {
        get => _grid;
        set
        {
            _grid = value;
            Songs.Visibility = value ? Visibility.Collapsed : Visibility.Visible;

            GridRows.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            ViewToggle.Content = value ? "☰ " + LibraryTexts.ListView(_language) : "▦ " + LibraryTexts.GridView(_language);
            SyncSelectionView();
        }
    }

    /// <summary>Aplica el idioma y deja el cursor en el buscador.</summary>
    public void Show(string language)
    {
        _language = language;
        TitleText.Text = LibraryTexts.Title(language);
        Placeholder.Text = LibraryTexts.SearchPlaceholder(language);
        HintText.Text = LibraryTexts.Hint(language);
        BackButton.Content = LibraryTexts.Back(language);
        SessionButton.Content = LibraryTexts.Session(language);
        QueueAddButton.Content = QueueTexts.Add(language);
        SetQueueCount(_queueCount);
        GridMode = _grid; // refresca la etiqueta del botón con el idioma
        RefreshStatus();
        Dispatcher.BeginInvoke(() => SearchBox.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Entrega la biblioteca completa (la búsqueda actual se vuelve a aplicar).</summary>
    public void SetSongs(IReadOnlyList<SongEntry> songs)
    {
        _scanned = -1;
        if (ReferenceEquals(songs, _all) && _results.Count > 0 && SearchBox.Text == _appliedSearch)
        {
            // La misma biblioteca de antes (p. ej. al volver de cantar): no se toca nada, así el desplazamiento
            // y la selección quedan exactamente donde estaban.
            RefreshStatus();
            return;
        }

        var keepPath = Current?.Path;
        var scroller = FindScroller(_grid ? GridRows : Songs);
        var offset = scroller?.VerticalOffset ?? 0;
        _all = songs;
        ApplyFilter();
        if (keepPath is not null && _results.FindIndex(s => s.Path == keepPath) is >= 0 and var index)
        {
            // La biblioteca cambió (se terminó de escanear): se vuelve a la misma canción y a la misma altura.
            Select(index);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FindScroller(_grid ? GridRows : Songs)?.ScrollToVerticalOffset(offset));
        }
    }

    private string _appliedSearch = string.Empty;

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScroller(child) is { } inner)
            {
                return inner;
            }
        }

        return null;
    }

    /// <summary>Muestra el avance de la búsqueda de canciones (<c>null</c> = terminada).</summary>
    public void SetScanProgress(int? found)
    {
        _scanned = found ?? -1;
        RefreshStatus();
    }

    private void ApplyFilter()
    {
        _appliedSearch = SearchBox.Text;
        _results = SongSearch.Filter(_all, SearchBox.Text);
        Songs.ItemsSource = _results;
        _cells = _results.Select((s, i) => new GridCell(s) { Index = i }).ToList();
        GridRows.ItemsSource = _cells.Chunk(GridColumns).Select(c => new GridRow(c)).ToList();
        _selected = -1;
        Select(_results.Count > 0 ? 0 : -1);

        CountText.Text = LibraryTexts.Count(_language, _results.Count, _all.Count);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var shown = _results.Count;
        StatusText.Text = _scanned >= 0 ? LibraryTexts.Scanning(_language, _scanned)
            : _all.Count == 0 ? LibraryTexts.EmptyLibrary(_language)
            : shown == 0 ? LibraryTexts.NoResults(_language)
            : string.Empty;
        FoldersButton.Visibility = _scanned < 0 && _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FoldersButton.Content = LibraryTexts.ChooseFolder(_language);
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Selección -----------------------------------------------------------------------------------------

    private void Select(int index)
    {
        if (_results.Count == 0)
        {
            index = -1;
        }
        else
        {
            index = Math.Clamp(index, 0, _results.Count - 1);
        }

        if (_selected >= 0 && _selected < _cells.Count)
        {
            _cells[_selected].IsSelected = false;
        }

        _selected = index;
        if (index >= 0)
        {
            _cells[index].IsSelected = true;
        }

        SyncSelectionView();
        UpdateDetail();
    }

    /// <summary>Lleva la selección a la vista activa (la lista usa su propia selección; la grilla, la celda).</summary>
    private void SyncSelectionView()
    {
        if (_selected < 0)
        {
            return;
        }

        if (_grid)
        {
            var row = _selected / GridColumns;
            if (GridRows.Items.Count > row)
            {
                GridRows.ScrollIntoView(GridRows.Items[row]);
                EnsureFullyVisible(GridRows, row);
            }
        }
        else
        {
            if (Songs.SelectedIndex != _selected)
            {
                Songs.SelectedIndex = _selected;
            }

            Songs.ScrollIntoView(Songs.Items[_selected]);
            EnsureFullyVisible(Songs, _selected);
        }
    }

    /// <summary>
    /// La fila activa siempre queda completa a la vista: tras el desplazamiento por pixel, se la trae entera
    /// (la última fila puede verse cortada solo cuando no es la activa).
    /// </summary>
    private void EnsureFullyVisible(ListBox list, int index) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
            {
                container.BringIntoView();
            }
        });

    // ---- Vista previa en video (en el lugar de la carátula grande) ----------------------------------------------

    private readonly DispatcherTimer _videoTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly System.Diagnostics.Stopwatch _videoClock = new();
    private TimeSpan _videoStart;
    private double _videoGap;
    private bool _videoActive;

    /// <summary>Muestra el video de la canción resaltada desde <paramref name="startSeconds"/> (siguiendo el reloj local).</summary>
    public void ShowPreviewVideo(string path, double gapSeconds, double startSeconds)
    {
        HidePreviewVideo();
        _videoActive = true;
        _videoGap = gapSeconds;
        _videoStart = TimeSpan.FromSeconds(startSeconds);
        PreviewVideo.Load(path);
        if (PreviewVideo.HasVideo)
        {
            _videoClock.Restart();
            _videoTimer.Start();
        }
        else
        {
            _videoActive = false;
        }
    }

    /// <summary>Quita el video y vuelve a mostrarse solo la carátula.</summary>
    public void HidePreviewVideo()
    {
        _videoActive = false;
        _videoTimer.Stop();
        _videoClock.Reset();
        PreviewVideo.Visibility = Visibility.Collapsed;
        PreviewVideo.Unload();
    }

    /// <summary>Libera el reproductor de video al cerrar la aplicación.</summary>
    public void DisposePreviewVideo() => PreviewVideo.Dispose();

    private SongEntry? Current => _selected >= 0 && _selected < _results.Count ? _results[_selected] : null;

    /// <summary>La canción resaltada cambió (por teclado, mouse o filtro); <c>null</c> si no hay ninguna.</summary>
    public event EventHandler<SongEntry?>? HighlightChanged;

    private void UpdateDetail()
    {
        HidePreviewVideo();
        HighlightChanged?.Invoke(this, Current);
        if (Current is not { } entry)
        {
            Detail.Visibility = Visibility.Hidden;
            return;
        }

        Detail.Visibility = Visibility.Visible;
        DetailTitle.Text = entry.Title;
        DetailArtist.Text = entry.Artist;
        BigCoverImage.Source = BigCover.Convert(entry.CoverPath, typeof(object), null!,
            System.Globalization.CultureInfo.CurrentCulture) as System.Windows.Media.ImageSource;
        DetailMeta.Text = string.Join(" · ", new[] { entry.Genre, entry.Year, entry.Language, entry.Edition }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        VideoBadge.Text = entry.HasVideo ? "▶ " + LibraryTexts.Video(_language) : string.Empty;
    }

    // ---- Eventos -------------------------------------------------------------------------------------------

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_grid && Songs.SelectedIndex >= 0 && Songs.SelectedIndex != _selected)
        {
            Select(Songs.SelectedIndex); // clic con el mouse en la lista
        }
    }

    private void OnSongDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Current is { } entry)
        {
            SongChosen?.Invoke(this, entry);
        }
    }

    private void OnCellMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GridCell cell })
        {
            Select(cell.Index);
            if (e.ClickCount >= 2)
            {
                SongChosen?.Invoke(this, cell.Song);
            }
        }
    }

    private void OnFoldersClick(object sender, RoutedEventArgs e) => FoldersRequested?.Invoke(this, EventArgs.Empty);

    private void OnViewToggleClick(object sender, RoutedEventArgs e) => ToggleView();

    private int _queueCount;

    /// <summary>Actualiza el contador del botón de la cola.</summary>
    public void SetQueueCount(int count)
    {
        _queueCount = count;
        QueueButton.Content = QueueTexts.Open(_language, count);
    }

    private void RequestQueueAdd()
    {
        if (Current is { } entry)
        {
            QueueAddRequested?.Invoke(this, entry);
        }
    }

    private void OnQueueAddClick(object sender, RoutedEventArgs e) => RequestQueueAdd();

    private void OnQueueClick(object sender, RoutedEventArgs e) => QueueRequested?.Invoke(this, EventArgs.Empty);

    private void OnSessionClick(object sender, RoutedEventArgs e) => SessionRequested?.Invoke(this, EventArgs.Empty);

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void ToggleView()
    {
        GridMode = !_grid;
        ViewModeChanged?.Invoke(this, EventArgs.Empty);
        SearchBox.Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var step = _grid ? GridColumns : 1;
        switch (e.Key)
        {
            case Key.Down:
                Select(_selected + step);
                break;
            case Key.Up:
                Select(_selected - step);
                break;
            case Key.Right when _grid:
                Select(_selected + 1);
                break;
            case Key.Left when _grid:
                Select(_selected - 1);
                break;
            case Key.PageDown:
                Select(_selected + (_grid ? GridColumns * 2 : 8));
                break;
            case Key.PageUp:
                Select(_selected - (_grid ? GridColumns * 2 : 8));
                break;
            case Key.Home when Keyboard.Modifiers == ModifierKeys.Control:
                Select(0);
                break;
            case Key.End when Keyboard.Modifiers == ModifierKeys.Control:
                Select(_results.Count - 1);
                break;
            case Key.Enter:
                if (Current is { } entry)
                {
                    SongChosen?.Invoke(this, entry);
                }

                break;
            case Key.Escape:
                if (SearchBox.Text.Length > 0)
                {
                    SearchBox.Clear();
                }
                else
                {
                    BackRequested?.Invoke(this, EventArgs.Empty);
                }

                break;
            case Key.F2:
                FoldersRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.F3:
                ToggleView();
                break;
            case Key.F4:
                SessionRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.F5:
                RequestQueueAdd();
                break;
            case Key.F6:
                QueueRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
