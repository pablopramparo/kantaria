// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kantor.App.Pixel;
using Kantor.App.Results;
using Kantor.Core.Characters;
using Kantor.Core.Data;
using Kantor.Core.Menu;

namespace Kantor.App.Players;

/// <summary>Lo que la pantalla de jugadores necesita de la aplicación.</summary>
public sealed class PlayersModel
{
    public required KantorStore Store { get; init; }

    public required string Language { get; init; }

    public required CharacterCatalog Catalog { get; init; }

    /// <summary>Personajes que carga el usuario (se agregan al <see cref="Catalog"/> al guardarlos).</summary>
    public required UserCharacterStore UserCharacters { get; init; }

    /// <summary>Selección actual: quién ocupa cada puesto P1–P4.</summary>
    public required Func<IReadOnlyList<SelectionEntry>> GetSelection { get; init; }

    /// <summary>Cambia la selección (la aplicación la persiste si hay sesión abierta).</summary>
    public required Action<IReadOnlyList<SelectionEntry>> SetSelection { get; init; }

    /// <summary>Puestos (0–3) con micrófono habilitado en Opciones: los únicos en los que se puede cantar.</summary>
    public required IReadOnlyList<int> EnabledSlots { get; init; }

    /// <summary>Nombres de las voces de la canción cargada; vacío (o una sola) si no es un dueto.</summary>
    public IReadOnlyList<string> VoiceNames { get; init; } = [];

    /// <summary>Voz (índice) que canta un puesto en la canción cargada.</summary>
    public Func<int, int> GetVoice { get; init; } = _ => 0;

    /// <summary>Cambia la voz de un puesto en la canción cargada.</summary>
    public Action<int, int> SetVoice { get; init; } = (_, _) => { };

    /// <summary>Qué micrófono es cada puesto, para mostrarlo junto al jugador elegido.</summary>
    public required Func<int, string> MicLabel { get; init; }

    /// <summary>
    /// ¿Se canta con la sesión abierta (<c>true</c>) o sin sesión (<c>false</c>: cualquier jugador del sistema y el
    /// resultado no se guarda)?
    /// </summary>
    public required bool UseSession { get; init; }

    public required Action<bool> SetUseSession { get; init; }

    /// <summary>Retrato del personaje (por ID) para mostrar al jugador; <c>null</c> si todavía no hay imagen.</summary>
    public required Func<string, ImageSource?> PortraitOf { get; init; }
}

/// <summary>
/// Jugadores, sesión y historial básico. Habla directamente con <see cref="KantorStore"/> y avisa cuando algo
/// cambió para que la aplicación actualice lo que muestra durante el canto.
/// </summary>
public partial class PlayersView : UserControl
{
    public enum Section
    {
        Players,
        Session,
        History,
    }

    private PlayersModel? _model;
    private Section _section = Section.Players;
    private string? _focusKey;
    private long? _editingId;
    private long? _confirmArchiveId;
    private int _historyMode;
    private int _globalDifficulty; // 0 = todas, 1–3 = fácil, medio, difícil
    private int _globalMinSongs;   // índice en MinSongsChoices
    private static readonly int[] MinSongsChoices = [1, 2, 3, 5, 10];
    private string? _historyNotice;
    private long? _confirmDeleteSessionId;
    private int _historyTarget;
    private string? _message;
    private TextBox? _nameBox;

    public event Action? DataChanged;

    public event EventHandler? BackRequested;

    /// <summary>En modo "¿quién canta?": hay al menos un jugador elegido y el usuario confirmó.</summary>
    public event EventHandler? ReadyRequested;

    /// <summary>No hay micrófonos habilitados: el usuario pide ir a Opciones › Audio para crearlos.</summary>
    public event EventHandler? OptionsRequested;

    private bool _pickMode;
    private bool _membersMode;

    public PlayersView()
    {
        InitializeComponent();
        PreviewKeyDown += OnKeyDown;
        TabsPanel.PreviewKeyDown += OnTabsKeyDown;
        ContentPanel.PreviewKeyDown += OnContentKeyDown;
    }

    private bool FocusActiveTab()
    {
        if (TabsPanel.Visibility == Visibility.Visible && FindByTag(TabsPanel, $"tab{(int)_section}") is { } tab)
        {
            tab.Focus();
            return true;
        }

        return false;
    }

    /// <summary>Con el foco en las pestañas: ← → cambian de sección; ↓ o Enter entran a las opciones.</summary>
    private void OnTabsKeyDown(object sender, KeyEventArgs e)
    {
        if (ChoiceButton.AnyOpen)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left or Key.Right:
                var count = Enum.GetValues<Section>().Length;
                _section = (Section)(((int)_section + (e.Key == Key.Right ? 1 : count - 1)) % count);
                _focusKey = null;
                _message = null;
                BuildTabs();
                BuildContent();
                e.Handled = true;
                break;
            case Key.Down or Key.Enter:
                FindFirstFocusable(ContentPanel)?.Focus();
                e.Handled = true;
                break;
        }
    }

    /// <summary>↑ desde la primera opción vuelve a las pestañas (cuando hay).</summary>
    private void OnContentKeyDown(object sender, KeyEventArgs e)
    {
        if (ChoiceButton.AnyOpen || e.Key != Key.Up || Keyboard.FocusedElement is not UIElement focused)
        {
            return;
        }

        if (!focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Up)))
        {
            FocusActiveTab();
        }

        e.Handled = true;
    }

    private string Lang => _model?.Language ?? "es";

    private string T(string key) => PlayersTexts.Get(Lang, key);

    /// <param name="notice">Mensaje a mostrar al abrir (por ejemplo, "elegí quién canta").</param>
    /// <param name="pick">Modo "¿quién canta?" al entrar a una canción (sin pestañas).</param>
    /// <param name="members">Modo "¿quién juega?" al empezar una partida nueva (sin pestañas).</param>
    public void Show(PlayersModel model, Section? section = null, string? notice = null, bool pick = false,
        bool members = false)
    {
        _model = model;
        _pickMode = pick;
        _membersMode = members;
        _choosingSlot = null;
        _draft = null;
        _facePicker = null;
        FaceLayer.Visibility = Visibility.Collapsed;
        _confirmDeleteCharacter = null;
        if (pick || members)
        {
            section = Section.Session;
        }

        if (section is { } wanted && wanted != _section)
        {
            _section = wanted;
            _focusKey = null;
        }

        _message = notice;
        _addingMember = false;
        TabsPanel.Visibility = pick || members ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Text = pick ? T("pick.title") : members ? T("members.title") : T("title");
        HintText.Text = T("hint");
        BackButton.Content = T("back");
        BuildTabs();
        BuildContent();
    }

    private void Focus(string key) => _focusKey = key;

    private void BuildTabs()
    {
        TabsPanel.Children.Clear();
        var names = new[] { "tab.players", "tab.session", "tab.history" };
        for (var i = 0; i < names.Length; i++)
        {
            var section = (Section)i;
            var button = PixelUi.Action($"tab{i}", T(names[i]), () =>
            {
                _section = section;
                _focusKey = null;
                _message = null;
                BuildTabs();
                BuildContent();
            }, 24);
            button.Margin = new Thickness(0, 0, 14, 0);
            button.MinWidth = 190;
            button.Foreground = section == _section ? PixelUi.Brush("GoldBrush") : PixelUi.Brush("TextBrush");
            TabsPanel.Children.Add(button);
        }
    }

    private void BuildContent()
    {
        ContentPanel.Children.Clear();
        ContentPanel.IsEnabled = true;
        TabsPanel.IsEnabled = true;
        ModalLayer.Visibility = Visibility.Collapsed;
        ModalPanel.Children.Clear();
        _nameBox = null;
        if (_model is null)
        {
            return;
        }

        try
        {
            switch (_section)
            {
                case Section.Players:
                    BuildPlayers(_model);
                    break;
                case Section.Session:
                    if (_membersMode)
                    {
                        BuildMembers(_model);
                    }
                    else
                    {
                        BuildSession(_model, _pickMode);
                    }

                    break;
                default:
                    BuildHistory(_model);
                    break;
            }
        }
        catch (Exception e) when (e is StoreRuleException or Microsoft.Data.Sqlite.SqliteException)
        {
            ContentPanel.Children.Add(PixelUi.Note(e.Message, 18));
        }

        if (_draft is not null)
        {
            BuildCharacterModal(_model, _draft);
        }
        else if (_addingMember)
        {
            try
            {
                BuildAddModal(_model);
            }
            catch (Exception e) when (e is StoreRuleException or Microsoft.Data.Sqlite.SqliteException)
            {
                _addingMember = false;
                _message = e.Message;
            }
        }

        if (_message is not null)
        {
            // Arriba de todo: abajo quedaba fuera de vista en las listas largas.
            var note = PixelUi.Note(_message, 20);
            note.Foreground = PixelUi.Brush("GoldBrush");
            ContentPanel.Children.Insert(0, note);
        }

        ContentScroll.ScrollToTop();
        Dispatcher.BeginInvoke(RestoreFocus, DispatcherPriority.Input);
    }

    private void RestoreFocus()
    {
        if (ModalLayer.Visibility == Visibility.Visible)
        {
            // el modal manda: el jugador actual o, si no, la primera tarjeta
            var inModal = _focusKey is not null ? FindByTag(ModalPanel, _focusKey) : null;
            (inModal ?? FindFirstFocusable(ModalPanel))?.Focus();
            return;
        }

        if (_focusKey is not null && FindByTag(ContentPanel, _focusKey) is { } target)
        {
            target.Focus();
            return;
        }

        // Sin un renglón recordado el foco queda en la pestaña activa: ← → cambian de sección, ↓ entra.
        if (_focusKey is null && FocusActiveTab())
        {
            return;
        }

        if (_nameBox is not null)
        {
            _nameBox.Focus();
            return;
        }

        FindFirstFocusable(ContentPanel)?.Focus();
    }

    private static FrameworkElement? FindByTag(DependencyObject root, string tag)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Tag: string t } fe && t == tag && fe.Focusable)
            {
                return fe;
            }

            if (FindByTag(child, tag) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static FrameworkElement? FindFirstFocusable(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { IsEnabled: true, Focusable: true } or TextBox { IsEnabled: true })
            {
                return (FrameworkElement)child;
            }

            if (FindFirstFocusable(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    /// <summary>
    /// Atrás (botón VOLVER o Esc): primero cierra lo que esté abierto encima (edición de nombre, modal de jugador
    /// de un micrófono, modal de agregar jugadores) y, si no hay nada, sale de la pantalla.
    /// </summary>
    private void GoBack()
    {
        if (_facePicker is not null)
        {
            CloseFacePicker();
        }
        else if (_editingId is not null)
        {
            _editingId = null;
            BuildContent();
        }
        else if (_draft is not null)
        {
            _draft = null;
            _focusKey = "charNew";
            BuildContent();
        }
        else if (_choosingSlot is not null)
        {
            _choosingSlot = null;
            BuildContent();
        }
        else if (_addingMember)
        {
            _addingMember = false;
            BuildContent();
        }
        else
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (ChoiceButton.AnyOpen)
        {
            return; // hay un select desplegado: Esc y las flechas son suyos
        }

        if (_facePicker is not null && e.Key != Key.Escape)
        {
            return; // la grilla de avatares maneja sus teclas (y RePág no debe cambiar de pestaña)
        }

        switch (e.Key)
        {
            case Key.Escape:
                GoBack();
                e.Handled = true;
                break;
            case Key.PageDown or Key.PageUp when !_pickMode && !_membersMode:
                var count = Enum.GetValues<Section>().Length;
                _section = (Section)(((int)_section + (e.Key == Key.PageDown ? 1 : count - 1)) % count);
                _focusKey = null;
                _message = null;
                BuildTabs();
                BuildContent();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Corre una acción sobre los datos mostrando el mensaje de una regla incumplida en vez de fallar.</summary>
    private void Apply(Action action)
    {
        try
        {
            _message = null;
            action();
            DataChanged?.Invoke();
        }
        catch (StoreRuleException e)
        {
            _message = e.Message;
        }
        catch (Microsoft.Data.Sqlite.SqliteException e)
        {
            _message = e.Message;
        }

        BuildContent();
    }

    // ---- Jugadores ----------------------------------------------------------------------------------------

    private void BuildPlayers(PlayersModel m)
    {
        var rename = _editingId is { } id ? m.Store.GetPlayer(id) : null;
        if (rename is null)
        {
            _editingId = null;
        }

        if (rename is not null)
        {
            ContentPanel.Children.Add(PixelUi.Note(PlayersTexts.Format(Lang, "p.editing", rename.Name), 18));
        }

        var box = PixelUi.Input("name", 420);
        box.Text = rename?.Name ?? string.Empty;
        box.ToolTip = T("p.name");
        box.GotKeyboardFocus += (_, _) => Focus("name");
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitName(m, box.Text);
                e.Handled = true;
            }
        };
        _nameBox = box;

        var commit = PixelUi.Action("commit", rename is null ? T("p.create") : T("p.save"),
            () => CommitName(m, box.Text), 20, Focus);
        commit.Margin = new Thickness(12, 0, 0, 0);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        line.Children.Add(box);
        line.Children.Add(commit);
        if (rename is not null)
        {
            var cancel = PixelUi.Action("cancel", T("p.cancel"), () =>
            {
                _editingId = null;
                BuildContent();
            }, 20, Focus);
            cancel.Margin = new Thickness(12, 0, 0, 0);
            line.Children.Add(cancel);
        }

        ContentPanel.Children.Add(line);

        var players = m.Store.ListPlayers();
        if (players.Count == 0)
        {
            ContentPanel.Children.Add(PixelUi.Note(T("p.none"), 18));
        }

        var faces = m.Catalog.All.Select(c => m.PortraitOf(c.Id)).ToList();
        foreach (var player in players)
        {
            var current = player;
            var characterIndex = Math.Max(0, m.Catalog.All.ToList().FindIndex(c => c.Id == current.CharacterId));

            var name = new TextBlock
            {
                Text = current.Name,
                FontFamily = PixelUi.Pixel,
                FontSize = 28,
                Foreground = PixelUi.Brush("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 320,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var character = new FaceChoice { Tag = $"char{current.Id}", Margin = new Thickness(0, 0, 12, 0) };
            character.Set(faces, characterIndex);
            character.GotKeyboardFocus += (_, _) => Focus($"char{current.Id}");
            character.OpenRequested += (_, _) => ShowFacePicker(character);
            character.Changed += (_, _) =>
                Apply(() => m.Store.SetCharacter(current.Id, m.Catalog.All[character.SelectedIndex].Id));
            var renameButton = PixelUi.Action($"ren{current.Id}", T("p.rename"), () =>
            {
                _editingId = current.Id;
                _focusKey = "name";
                _confirmArchiveId = null;
                BuildContent();
            }, 20, Focus);
            renameButton.Margin = new Thickness(10, 0, 0, 0);
            var confirming = _confirmArchiveId == current.Id;
            var archive = PixelUi.Action($"arc{current.Id}", confirming ? T("p.confirm") : T("p.archive"), () =>
            {
                if (_confirmArchiveId == current.Id)
                {
                    _confirmArchiveId = null;
                    Apply(() => m.Store.ArchivePlayer(current.Id));
                    SyncSelectionAfterDataChange(m);
                }
                else
                {
                    _confirmArchiveId = current.Id;
                    BuildContent();
                }
            }, 20, Focus);
            archive.Margin = new Thickness(10, 0, 0, 0);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            row.Children.Add(name);
            row.Children.Add(character);
            row.Children.Add(renameButton);
            row.Children.Add(archive);
            ContentPanel.Children.Add(row);
        }

        ContentPanel.Children.Add(PixelUi.Note(T("p.help")));
        BuildCharactersSection(m);
    }

    private void CommitName(PlayersModel m, string text)
    {
        var editing = _editingId;
        Apply(() =>
        {
            if (editing is { } id)
            {
                m.Store.RenamePlayer(id, text);
                _editingId = null;
            }
            else
            {
                m.Store.CreatePlayer(text);
            }
        });
        if (_message is null)
        {
            _focusKey = "name";
        }
    }

    /// <summary>Un jugador archivado ya no puede estar elegido: se le quita su puesto.</summary>
    private static void SyncSelectionAfterDataChange(PlayersModel m)
    {
        var selection = m.GetSelection();
        var active = m.Store.ListPlayers().Select(p => p.Id).ToHashSet();
        var kept = selection.Where(s => active.Contains(s.PlayerId)).ToList();
        if (kept.Count != selection.Count)
        {
            m.SetSelection(kept);
        }
    }

    // ---- Sesión -------------------------------------------------------------------------------------------

    /// <param name="pick">
    /// Modo "¿quién canta?" al entrar a una canción: solo elegir entre los jugadores de la sesión para los
    /// micrófonos habilitados (y sumar o crear jugadores si faltan).
    /// </param>
    private void BuildSession(PlayersModel m, bool pick = false)
    {
        var session = m.Store.GetOpenSession();
        if (pick)
        {
            ContentPanel.Children.Add(PixelUi.Note(T("pick.need"), 20));
        }
        else if (session is null)
        {
            ContentPanel.Children.Add(PixelUi.Note(T("s.none"), 18));
        }
        else
        {
            var count = m.Store.GetSessionHistory(session.Id).Count;
            var started = session.StartedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            var label = string.IsNullOrWhiteSpace(session.Name) ? string.Empty : $"“{session.Name}” · ";
            ContentPanel.Children.Add(PixelUi.Note(
                label + PlayersTexts.Format(Lang, "s.open", started, PlayersTexts.Songs(Lang, count)), 18));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        buttons.Children.Add(PixelUi.Action("newSession", T("s.new"), () => Apply(() =>
        {
            m.Store.StartSession(); // cierra la abierta, si había
            m.SetUseSession(true);
            m.SetSelection([]); // la sesión nueva no hereda participantes
        }), 20, Focus));
        if (session is not null)
        {
            var close = PixelUi.Action("closeSession", T("s.close"), () => Apply(() =>
            {
                m.Store.CloseSession(session.Id);
                m.SetSelection([]);
            }), 20, Focus);
            close.Margin = new Thickness(12, 0, 0, 0);
            buttons.Children.Add(close);
        }

        if (!pick)
        {
            ContentPanel.Children.Add(buttons);
        }

        IReadOnlyList<PlayerRecord> source;
        if (session is not null)
        {
            source = m.Store.SessionPlayers(session.Id);

            // Jugadores de la partida (tarjetas con retrato y "Quitar") y "Agregar jugador", que abre un modal.
            // Eligiendo quién canta suelto no se toca la partida abierta.
            if (!(pick && !m.UseSession))
            {
                AddMembersBlock(m, session, showTiles: !pick);
            }
        }
        else
        {
            source = m.Store.ListPlayers();
        }

        // Cantar sin sesión (o sin sesión abierta): cualquier jugador del sistema.
        if (!m.UseSession || session is null)
        {
            source = m.Store.ListPlayers();
        }

        if (pick)
        {
            // Quién canta en cada micrófono se elige por canción: solo acá, al entrar a cada una.
            AddParticipantRows(m, source);
            AddPickerFooter(m, m.UseSession ? session : null);
            if (_choosingSlot is { } choosing)
            {
                BuildChooser(m, source, choosing);
            }
        }
        else
        {
            if (session is not null)
            {
                AddStandings(m, session);
            }

            ContentPanel.Children.Add(PixelUi.Note(T("s.persong"), 18));
            var done = PixelUi.Action("ready", T("s.ready"), () => BackRequested?.Invoke(this, EventArgs.Empty), 24, Focus);
            done.HorizontalAlignment = HorizontalAlignment.Left;
            done.Margin = new Thickness(0, 12, 0, 0);
            ContentPanel.Children.Add(done);
        }
    }

    /// <summary>Clasificación de la partida abierta: total, promedio, mejor puntaje, canciones y victorias.</summary>
    private void AddStandings(PlayersModel m, SessionRecord session)
    {
        ContentPanel.Children.Add(PixelUi.Heading(T("st.title")));
        AddStandingLines(RankingStats.Compute(m.Store.GetSessionHistory(session.Id)));
    }

    /// <summary>Una línea por jugador (puesto, competición, efectividad, puntos, canciones, victorias) y las reglas.</summary>
    private void AddStandingLines(IReadOnlyList<PlayerStanding> standings)
    {
        if (standings.Count == 0)
        {
            ContentPanel.Children.Add(PixelUi.Note(T("st.none"), 18));
            return;
        }

        foreach (var s in standings)
        {
            ContentPanel.Children.Add(HistoryLine(PlayersTexts.Format(Lang, "st.line", s.Position, s.Name,
                s.CompetitionPoints, s.Effectiveness.ToString("0.0", CultureInfo.CurrentCulture),
                s.Total.ToString("N0"), s.Songs, s.Wins)));
        }

        ContentPanel.Children.Add(PixelUi.Note(T("st.rules"), 15));
    }

    /// <summary>
    /// Un renglón por micrófono habilitado (Opciones › Audio): quién canta ahí. La sesión puede tener más de
    /// cuatro jugadores; en cada canción cantan, como mucho, tantos como micrófonos haya.
    /// </summary>
    private void AddParticipantRows(PlayersModel m, IReadOnlyList<PlayerRecord> source)
    {
        if (source.Count == 0)
        {
            ContentPanel.Children.Add(PixelUi.Note(T("p.none"), 18));
        }

        if (m.EnabledSlots.Count == 0)
        {
            var warning = PixelUi.Note(T("s.nomics"), 18);
            warning.Foreground = PixelUi.Brush("GoldBrush");
            ContentPanel.Children.Add(warning);
            var options = PixelUi.Action("toOptions", T("s.tooptions"), () => OptionsRequested?.Invoke(this, EventArgs.Empty),
                22, Focus);
            options.HorizontalAlignment = HorizontalAlignment.Left;
            options.Margin = new Thickness(0, 8, 0, 8);
            ContentPanel.Children.Add(options);
            return;
        }

        // Una tarjeta por micrófono con el jugador que lo tiene; al tocarla se abre la elección visual.
        var selection = m.GetSelection();
        var cards = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        foreach (var enabledSlot in m.EnabledSlots)
        {
            var entry = selection.FirstOrDefault(s => s.Slot == enabledSlot + 1);
            var player = entry.PlayerId == 0 ? null : source.FirstOrDefault(p => p.Id == entry.PlayerId);
            var slotIndex = enabledSlot;
            var card = new Button
            {
                Tag = $"mic{enabledSlot}",
                Margin = new Thickness(0, 0, 20, 14),
                Width = 220,
                Height = 214,
                ToolTip = m.MicLabel(enabledSlot),
            };
            card.SetResourceReference(StyleProperty, "PixelButton");
            card.GotKeyboardFocus += (_, _) => Focus($"mic{slotIndex}");
            card.Click += (_, _) =>
            {
                _choosingSlot = slotIndex;
                _focusKey = null;
                BuildContent();
            };
            card.Content = BuildMicCard(m, enabledSlot, player);
            System.Windows.Automation.AutomationProperties.SetName(card,
                $"{PlayersTexts.Format(Lang, "s.slot", enabledSlot + 1)}: {player?.Name ?? T("pick.choose")}");
            var column = new StackPanel { Margin = new Thickness(0, 0, 20, 14) };
            card.Margin = new Thickness(0, 0, 0, 8);
            column.Children.Add(card);
            if (m.VoiceNames.Count > 1)
            {
                // dueto: qué voz canta este micrófono (por omisión, el reparto automático)
                var labels = m.VoiceNames
                    .Select((name, i) => string.IsNullOrWhiteSpace(name) || name == $"P{i + 1}"
                        ? PlayersTexts.Format(Lang, "s.voice", i + 1)
                        : $"{PlayersTexts.Format(Lang, "s.voice", i + 1)} · {name}")
                    .ToList();
                var voice = PixelUi.Choice($"voice{slotIndex}", labels, m.GetVoice(slotIndex), i =>
                {
                    m.SetVoice(slotIndex, i);
                    BuildContent();
                }, Focus);
                voice.Width = 220;
                column.Children.Add(voice);
            }

            cards.Children.Add(column);
        }

        ContentPanel.Children.Add(cards);
    }

    /// <summary>
    /// Tile cuadrado de un micrófono: "MIC n" arriba, el retrato del jugador asignado en el medio y su nombre
    /// abajo (o "Elegir jugador" si todavía no hay).
    /// </summary>
    private FrameworkElement BuildMicCard(PlayersModel m, int slot, PlayerRecord? player)
    {
        var color = PlayerColors.For(slot + 1);
        var title = new TextBlock
        {
            Text = PlayersTexts.Format(Lang, "s.slot", slot + 1),
            FontFamily = PixelUi.Pixel,
            FontSize = 34,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var avatar = PixelUi.Avatar(player is null ? null : m.PortraitOf(player.CharacterId), color, 92,
            empty: player is null);
        avatar.Margin = new Thickness(0, 4, 0, 6);
        avatar.HorizontalAlignment = HorizontalAlignment.Center;
        var name = new TextBlock
        {
            Text = player?.Name ?? T("pick.choose"),
            FontFamily = PixelUi.Pixel,
            FontSize = 28,
            Foreground = player is null ? PixelUi.Brush("DimBrush") : PixelUi.Brush("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 190,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(title);
        stack.Children.Add(avatar);
        stack.Children.Add(name);
        return stack;
    }

    /// <summary>
    /// Modal para elegir el jugador de un micrófono: una tarjeta (retrato y nombre) por jugador y la opción de
    /// dejarlo libre. Se dibuja sobre la pantalla; Esc o "Volver" lo cierran.
    /// </summary>
    private void BuildChooser(PlayersModel m, IReadOnlyList<PlayerRecord> source, int slot)
    {
        ModalPanel.Children.Clear();
        ModalPanel.Children.Add(new TextBlock
        {
            Text = PlayersTexts.Format(Lang, "pick.who", slot + 1),
            FontFamily = PixelUi.Pixel,
            FontSize = 38,
            Foreground = PixelUi.Brush("GoldBrush"),
            Margin = new Thickness(0, 0, 0, 14),
        });
        var current = m.GetSelection().FirstOrDefault(s => s.Slot == slot + 1).PlayerId;
        var color = PlayerColors.For(slot + 1);

        void Assign(PlayerRecord? player) => Apply(() =>
        {
            var next = m.GetSelection().Where(s => s.Slot != slot + 1).ToList();
            if (player is not null)
            {
                next.RemoveAll(s => s.PlayerId == player.Id); // un jugador ocupa un solo micrófono
                next.Add(new SelectionEntry(slot + 1, player.Id));
            }

            m.SetSelection([.. next.OrderBy(s => s.Slot)]);
            _choosingSlot = null;
        });

        Button Card(string key, FrameworkElement avatar, string text, bool isCurrent, Action onClick)
        {
            var label = new TextBlock
            {
                Text = text,
                FontFamily = PixelUi.Pixel,
                FontSize = 26,
                Foreground = isCurrent ? PixelUi.Brush("GoldBrush") : PixelUi.Brush("TextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 150,
            };
            avatar.Margin = new Thickness(0, 4, 0, 8);
            avatar.HorizontalAlignment = HorizontalAlignment.Center;
            var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            column.Children.Add(avatar);
            column.Children.Add(label);
            var button = new Button
            {
                Content = column,
                Tag = key,
                Margin = new Thickness(0, 0, 16, 16),
                Width = 190,
                Height = 190,
            };
            button.SetResourceReference(StyleProperty, "PixelButton");
            System.Windows.Automation.AutomationProperties.SetName(button, text);
            button.GotKeyboardFocus += (_, _) => Focus(key);
            button.Click += (_, _) => onClick();
            return button;
        }

        var wrap = new WrapPanel();
        foreach (var player in source)
        {
            var candidate = player;
            wrap.Children.Add(Card($"pl{candidate.Id}",
                PixelUi.Avatar(m.PortraitOf(candidate.CharacterId), color, 72, empty: false),
                candidate.Name, candidate.Id == current, () => Assign(candidate)));
        }

        wrap.Children.Add(Card("pl0", PixelUi.Avatar(null, color, 72, empty: true), T("pick.nobody"), current == 0,
            () => Assign(null)));
        var scroll = new ScrollViewer
        {
            Content = wrap,
            MaxHeight = 400,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar),
            (Style)Application.Current.FindResource("PixelScrollBar"));
        ModalPanel.Children.Add(scroll);

        var back = PixelUi.Action("chooseBack", T("pick.back"), () =>
        {
            _choosingSlot = null;
            BuildContent();
        }, 22, Focus);
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.Margin = new Thickness(0, 6, 0, 0);
        ModalPanel.Children.Add(back);

        // Mientras el modal está abierto, lo de abajo no recibe foco ni clics.
        ContentPanel.IsEnabled = false;
        TabsPanel.IsEnabled = false;
        ModalLayer.Visibility = Visibility.Visible;
    }

    private int? _choosingSlot;

    /// <summary>Pie del modo "¿quién canta?": confirmar (y crear jugadores si se canta suelto, sin partida).</summary>
    private void AddPickerFooter(PlayersModel m, SessionRecord? session)
    {
        if (session is null)
        {
            AddCreateRow(m, null);
        }

        AddPickerReady(m);
    }

    /// <summary>"¿Quién juega?": los jugadores de la partida y "Agregar jugador" (modal con selector y alta).</summary>
    private void BuildMembers(PlayersModel m)
    {
        var session = m.Store.GetOpenSession();
        if (session is null)
        {
            ContentPanel.Children.Add(PixelUi.Note(T("s.none"), 18));
            return;
        }

        ContentPanel.Children.Add(PixelUi.Note(T("members.help"), 20));
        AddMembersBlock(m, session, showTiles: true, heading: false);

        var ready = PixelUi.Action("ready", T("s.ready"), () =>
        {
            if (m.Store.SessionPlayers(session.Id).Count == 0)
            {
                _message = T("members.need");
                BuildContent();
                return;
            }

            ReadyRequested?.Invoke(this, EventArgs.Empty);
        }, 28, Focus);
        ready.HorizontalAlignment = HorizontalAlignment.Left;
        ready.Margin = new Thickness(0, 8, 0, 0);
        ContentPanel.Children.Add(ready);
    }

    private bool _addingMember;

    /// <summary>
    /// Jugadores de la partida como tarjetas (retrato, nombre y "Quitar") y el botón "Agregar jugador", que abre
    /// el modal. Sin <paramref name="showTiles"/> solo va el botón.
    /// </summary>
    private void AddMembersBlock(PlayersModel m, SessionRecord session, bool showTiles, bool heading = true)
    {
        if (showTiles)
        {
            var members = m.Store.SessionPlayers(session.Id);
            if (heading)
            {
                ContentPanel.Children.Add(PixelUi.Heading(T("s.players")));
            }

            if (members.Count == 0)
            {
                ContentPanel.Children.Add(PixelUi.Note(T("s.noplayers"), 18));
            }

            var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
            foreach (var member in members)
            {
                var current = member;
                var face = PixelUi.Avatar(m.PortraitOf(current.CharacterId), PlayerColors.For(1), 64);
                face.HorizontalAlignment = HorizontalAlignment.Center;
                var name = new TextBlock
                {
                    Text = current.Name,
                    FontFamily = PixelUi.Pixel,
                    FontSize = 24,
                    Foreground = PixelUi.Brush("TextBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 140,
                    Margin = new Thickness(0, 6, 0, 6),
                };
                var remove = PixelUi.Action($"rm{current.Id}", T("s.remove"), () => Apply(() =>
                {
                    m.Store.RemovePlayerFromSession(session.Id, current.Id);
                    m.SetSelection(m.GetSelection().Where(s => s.PlayerId != current.Id).ToList());
                }), 16, Focus);
                var tile = new StackPanel { Width = 150, Margin = new Thickness(0, 0, 18, 14) };
                tile.Children.Add(face);
                tile.Children.Add(name);
                tile.Children.Add(remove);
                wrap.Children.Add(tile);
            }

            ContentPanel.Children.Add(wrap);
        }

        var add = PixelUi.Action("addMember", T("s.addplayer"), () =>
        {
            _addingMember = true;
            _focusKey = null;
            BuildContent();
        }, 22, Focus);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 6, 0, 6);
        ContentPanel.Children.Add(add);
    }

    /// <summary>
    /// Modal "Agregar jugadores": los jugadores del sistema que todavía no están en la partida como tarjetas
    /// (un clic los suma y el modal sigue abierto) y, abajo, el alta de un jugador nuevo con nombre y avatar.
    /// </summary>
    private void BuildAddModal(PlayersModel m)
    {
        var session = m.Store.GetOpenSession();
        if (session is null)
        {
            _addingMember = false;
            return;
        }

        ModalPanel.Children.Clear();
        ModalPanel.Children.Add(new TextBlock
        {
            Text = T("members.add.title"),
            FontFamily = PixelUi.Pixel,
            FontSize = 38,
            Foreground = PixelUi.Brush("GoldBrush"),
            Margin = new Thickness(0, 0, 0, 12),
        });

        var inSession = m.Store.SessionPlayers(session.Id).Select(p => p.Id).ToHashSet();
        var candidates = m.Store.ListPlayers().Where(p => !inSession.Contains(p.Id)).ToList();
        if (candidates.Count == 0)
        {
            ModalPanel.Children.Add(PixelUi.Note(T("members.add.none"), 18));
        }
        else
        {
            var wrap = new WrapPanel();
            foreach (var candidate in candidates)
            {
                var player = candidate;
                var face = PixelUi.Avatar(m.PortraitOf(player.CharacterId), PlayerColors.For(1), 72);
                face.HorizontalAlignment = HorizontalAlignment.Center;
                var label = new TextBlock
                {
                    Text = player.Name,
                    FontFamily = PixelUi.Pixel,
                    FontSize = 26,
                    Foreground = PixelUi.Brush("TextBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 150,
                    Margin = new Thickness(0, 8, 0, 0),
                };
                var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                column.Children.Add(face);
                column.Children.Add(label);
                var card = new Button
                {
                    Content = column,
                    Tag = $"add{player.Id}",
                    Margin = new Thickness(0, 0, 16, 16),
                    Width = 180,
                    Height = 170,
                };
                card.SetResourceReference(StyleProperty, "PixelButton");
                System.Windows.Automation.AutomationProperties.SetName(card, player.Name);
                card.GotKeyboardFocus += (_, _) => Focus($"add{player.Id}");
                card.Click += (_, _) => Apply(() => m.Store.AddPlayerToSession(session.Id, player.Id));
                wrap.Children.Add(card);
            }

            var scroll = new ScrollViewer
            {
                Content = wrap,
                MaxHeight = 230,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
            };
            scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar),
                (Style)Application.Current.FindResource("PixelScrollBar"));
            ModalPanel.Children.Add(scroll);
        }

        AddCreateRow(m, session, ModalPanel);

        var close = PixelUi.Action("addClose", T("s.ready"), () =>
        {
            _addingMember = false;
            BuildContent();
        }, 24, Focus);
        close.HorizontalAlignment = HorizontalAlignment.Left;
        close.Margin = new Thickness(0, 4, 0, 0);
        ModalPanel.Children.Add(close);

        ContentPanel.IsEnabled = false;
        TabsPanel.IsEnabled = false;
        ModalLayer.Visibility = Visibility.Visible;
    }

    /// <summary>Crear un jugador nuevo (nombre y avatar); si hay partida, entra a ella.</summary>
    private void AddCreateRow(PlayersModel m, SessionRecord? session, Panel? target = null)
    {
        var panel = target ?? ContentPanel;
        var label = PixelUi.Note(T("p.new"), 18);
        label.Foreground = PixelUi.Brush("GoldBrush");
        panel.Children.Add(label);
        var box = PixelUi.Input("newName", 320);
        box.GotKeyboardFocus += (_, _) => Focus("newName");

        // Avatar del jugador nuevo: se elige entre los personajes del catálogo, con vista previa.
        var characters = m.Catalog.All;
        var characterChoice = new FaceChoice { Tag = "newChar", Margin = new Thickness(12, 0, 0, 0) };
        characterChoice.Set(characters.Select(c => m.PortraitOf(c.Id)).ToList(), 0);
        characterChoice.GotKeyboardFocus += (_, _) => Focus("newChar");
        characterChoice.OpenRequested += (_, _) => ShowFacePicker(characterChoice);

        void Create()
        {
            var text = box.Text;
            var characterId = characters[Math.Clamp(characterChoice.SelectedIndex, 0, characters.Count - 1)].Id;
            Apply(() =>
            {
                var created = m.Store.CreatePlayer(text, characterId);
                if (session is not null)
                {
                    m.Store.AddPlayerToSession(session.Id, created.Id); // el que se crea acá entra a la sesión
                }
            });
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Create();
                e.Handled = true;
            }
        };
        var create = PixelUi.Action("newGo", T("p.create"), Create, 20, Focus);
        create.Margin = new Thickness(12, 0, 0, 0);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        line.Children.Add(box);
        line.Children.Add(characterChoice);
        line.Children.Add(create);
        panel.Children.Add(line);
    }

    private void AddPickerReady(PlayersModel m)
    {
        var ready = PixelUi.Action("ready", T("s.ready"), () =>
        {
            var valid = m.GetSelection().Where(s => m.EnabledSlots.Contains(s.Slot - 1)).ToList();
            if (valid.Count == 0)
            {
                _message = T("pick.need");
                BuildContent();
                return;
            }

            ReadyRequested?.Invoke(this, EventArgs.Empty);
        }, 28, Focus);
        ready.HorizontalAlignment = HorizontalAlignment.Left;
        ready.Margin = new Thickness(0, 8, 0, 0);
        ContentPanel.Children.Add(ready);
    }

    // ---- Historial ----------------------------------------------------------------------------------------

    private void BuildHistory(PlayersModel m)
    {
        var modes = new[] { T("h.bysession"), T("h.byplayer"), T("h.global") };
        ContentPanel.Children.Add(PixelUi.Row(T("h.view"), PixelUi.Choice("hmode", modes, _historyMode, i =>
        {
            _historyMode = i;
            _historyTarget = 0;
            BuildContent();
        }, Focus)));

        AddHistoryTools(m);
        if (_historyNotice is not null)
        {
            ContentPanel.Children.Add(PixelUi.Note(_historyNotice, 18));
            _historyNotice = null;
        }

        if (_historyMode == 2)
        {
            ContentPanel.Children.Add(PixelUi.Heading(T("st.global")));
            var difficulties = new[] { T("h.f.all"), T("h.easy"), T("h.medium"), T("h.hard") };
            ContentPanel.Children.Add(PixelUi.Row(T("h.f.diff"), PixelUi.Choice("gdiff", difficulties, _globalDifficulty,
                i =>
                {
                    _globalDifficulty = i;
                    BuildContent();
                }, Focus)));
            ContentPanel.Children.Add(PixelUi.Row(T("h.f.min"), PixelUi.Choice("gmin",
                MinSongsChoices.Select(n => n.ToString(CultureInfo.CurrentCulture)).ToList(), _globalMinSongs, i =>
                {
                    _globalMinSongs = i;
                    BuildContent();
                }, Focus)));
            var all = m.Store.GetAllHistory()
                .Where(v => _globalDifficulty == 0 || v.Performance.Difficulty == _globalDifficulty - 1).ToList();
            AddStandingLines(RankingStats.Compute(all, MinSongsChoices[Math.Clamp(_globalMinSongs, 0, MinSongsChoices.Length - 1)]));
            return;
        }

        if (_historyMode == 0)
        {
            var sessions = m.Store.ListSessions();
            if (sessions.Count == 0)
            {
                ContentPanel.Children.Add(PixelUi.Note(T("h.none"), 18));
                return;
            }

            _historyTarget = Math.Clamp(_historyTarget, 0, sessions.Count - 1);
            var labels = sessions.Select(s =>
                $"{(string.IsNullOrWhiteSpace(s.Name) ? T("h.unnamed") : s.Name)} · " +
                $"{s.StartedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}" +
                (s.IsOpen ? $" · {T("h.open")}" : string.Empty)).ToList();
            ContentPanel.Children.Add(PixelUi.Row(T("h.pick"), PixelUi.Choice("htarget", labels, _historyTarget, i =>
            {
                _historyTarget = i;
                BuildContent();
            }, Focus), 640));

            var chosen = sessions[_historyTarget];
            AddSessionTools(m, chosen);
            var performances = m.Store.GetSessionHistory(chosen.Id);
            if (performances.Count == 0)
            {
                ContentPanel.Children.Add(PixelUi.Note(T("h.noitems"), 18));
            }
            else
            {
                ContentPanel.Children.Add(PixelUi.Heading(T("st.title")));
                AddStandingLines(RankingStats.Compute(performances));
            }

            foreach (var view in performances)
            {
                ContentPanel.Children.Add(PerformanceBlock(view));
            }

            return;
        }

        var players = m.Store.ListPlayers(includeArchived: true);
        if (players.Count == 0)
        {
            ContentPanel.Children.Add(PixelUi.Note(T("p.none"), 18));
            return;
        }

        _historyTarget = Math.Clamp(_historyTarget, 0, players.Count - 1);
        var names = players.Select(p => p.Archived ? $"{p.Name} ({T("h.archived")})" : p.Name).ToList();
        ContentPanel.Children.Add(PixelUi.Row(T("h.pick"), PixelUi.Choice("htarget", names, _historyTarget, i =>
        {
            _historyTarget = i;
            BuildContent();
        }, Focus), 640));

        var rows = m.Store.GetPlayerHistory(players[_historyTarget].Id);
        if (rows.Count == 0)
        {
            ContentPanel.Children.Add(PixelUi.Note(T("h.noitems"), 18));
        }

        foreach (var row in rows.Take(60))
        {
            var p = row.Performance;
            ContentPanel.Children.Add(HistoryLine(
                $"{p.FinishedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} · {p.Artist} — {p.Title} · " +
                $"{Difficulty(p.Difficulty)} · P{row.Slot} · {row.Score:N0} pts · {row.Position}º"));
        }
    }

    /// <summary>Exportar todo y respaldar la base: disponibles en cualquier vista del historial.</summary>
    private void AddHistoryTools(PlayersModel m)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(PixelUi.Action("exportAll", T("h.exportall"),
            () => ExportCsv(m, m.Store.GetAllHistory(), "kantor-historial"), 18, Focus));
        var backup = PixelUi.Action("backup", T("h.backup"), () => Backup(m), 18, Focus);
        backup.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(backup);
        ContentPanel.Children.Add(row);
    }

    /// <summary>Exportar y borrar la partida elegida (la abierta no se borra).</summary>
    private void AddSessionTools(PlayersModel m, SessionRecord session)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(PixelUi.Action("exportSession", T("h.exportsession"),
            () => ExportCsv(m, m.Store.GetSessionHistory(session.Id), "kantor-partida"), 18, Focus));
        if (session.IsOpen)
        {
            ContentPanel.Children.Add(row);
            ContentPanel.Children.Add(PixelUi.Note(T("h.deleteopen"), 15));
            return;
        }

        var confirming = _confirmDeleteSessionId == session.Id;
        var delete = PixelUi.Action("deleteSession", confirming ? T("p.confirm") : T("h.delete"), () =>
        {
            if (_confirmDeleteSessionId == session.Id)
            {
                _confirmDeleteSessionId = null;
                _historyNotice = m.Store.DeleteSession(session.Id) ? T("h.deleted") : null;
                _historyTarget = 0;
            }
            else
            {
                _confirmDeleteSessionId = session.Id;
            }

            BuildContent();
        }, 18, Focus);
        delete.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(delete);
        ContentPanel.Children.Add(row);
    }

    private void ExportCsv(PlayersModel m, IReadOnlyList<PerformanceView> performances, string baseName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"{baseName}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // UTF-8 con BOM para que Excel respete los acentos
            System.IO.File.WriteAllText(dialog.FileName, HistoryExport.ToCsv(performances, m.Store.ListSessions()),
                new System.Text.UTF8Encoding(true));
            _historyNotice = PlayersTexts.Format(Lang, "h.saved", dialog.FileName);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            _historyNotice = PlayersTexts.Format(Lang, "h.failed", e.Message);
        }

        BuildContent();
    }

    private void Backup(PlayersModel m)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "SQLite (*.db)|*.db",
            FileName = $"kantor-respaldo-{DateTime.Now:yyyyMMdd-HHmm}.db",
            DefaultExt = ".db",
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            m.Store.BackupTo(dialog.FileName);
            _historyNotice = PlayersTexts.Format(Lang, "h.saved", dialog.FileName) + "  ·  " + T("h.restore");
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException
                                      or Microsoft.Data.Sqlite.SqliteException)
        {
            _historyNotice = PlayersTexts.Format(Lang, "h.failed", e.Message);
        }

        BuildContent();
    }

    private FrameworkElement PerformanceBlock(PerformanceView view)
    {
        var p = view.Performance;
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(new TextBlock
        {
            Text = $"{p.Artist} — {p.Title}",
            FontFamily = PixelUi.Pixel,
            FontSize = 26,
            Foreground = PixelUi.Brush("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(PixelUi.Note(
            $"{p.FinishedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} · {Difficulty(p.Difficulty)}"));
        foreach (var result in view.Results.OrderBy(r => r.Position).ThenBy(r => r.Slot))
        {
            panel.Children.Add(HistoryLine($"{result.Position}º   P{result.Slot}   {result.PlayerName}   {result.Score:N0}"));
        }

        return panel;
    }

    private TextBlock HistoryLine(string text) => new()
    {
        Text = text,
        FontFamily = PixelUi.Body,
        FontSize = 17,
        Foreground = PixelUi.Brush("TextBrush"),
        Margin = new Thickness(0, 2, 0, 2),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private string Difficulty(int value) => T(value switch { 1 => "h.medium", 2 => "h.hard", _ => "h.easy" });
}
