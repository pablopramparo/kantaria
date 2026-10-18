// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kantor.App.Audio;
using Kantor.App.Pixel;
using Kantor.App.Results;
using Kantor.Core.Audio;
using Kantor.Core.Menu;
using Kantor.Core.Pitch;
using Kantor.Core.Settings;

namespace Kantor.App.Options;

/// <summary>
/// Pantalla de opciones: audio (motor y micrófonos), biblioteca, juego y pantalla. No guarda nada: muestra un
/// <see cref="OptionsModel"/> y avisa cada cambio por eventos; quien la aloja aplica el cambio y vuelve a llamar
/// a <see cref="Show"/> con el modelo actualizado.
/// </summary>
/// <remarks>
/// Un micrófono es un dispositivo más uno de sus canales (hasta <see cref="MicrophoneMap.MaxSlots"/>). Los que
/// se crean acá son los que están disponibles durante el juego.
/// </remarks>
public partial class OptionsView : UserControl
{
    public enum Section
    {
        Audio,
        Library,
        Game,
        Screen,
        Help,
        Credits,
    }

    private const double MinDb = -60;
    private static readonly TimeSpan VoiceHold = TimeSpan.FromMilliseconds(250);
    private static readonly double[] NonsenseSteps = [0, 0.01, 0.03, 0.05, 0.10];

    private OptionsModel? _model;
    private ActiveInput? _attachedInput;
    private Section _section = Section.Audio;
    private string? _focusKey;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<MicRow> _micRows = [];
    private IReadOnlyList<AudioInputDevice> _devices = [];
    private StackPanel? _micsHost;
    private Button? _createButton;
    private TextBlock? _status;

    // ---- Avisos al anfitrión ------------------------------------------------------------------------------

    public event Action<AudioEngineKind>? EngineRequested;

    public event Action<string>? AsioDriverRequested;

    public event Action<int>? OutputPairRequested;

    /// <summary>Puesto y retardo nuevo, en milisegundos.</summary>
    public event Action<int, int>? DelayChanged;

    public event Action<int>? CalibrateRequested;

    public event Action? AddFolderRequested;

    public event Action<string>? RemoveFolderRequested;

    public event Action<bool>? GridChanged;

    public event Action<int>? DifficultyChanged;

    public event Action<int>? VoiceDisplayChanged;

    public event Action<string>? LanguageChanged;

    public event Action<double>? NonsenseChanged;

    public event Action<bool>? SongPreviewChanged;

    public event Action<bool>? PhraseFeedbackChanged;

    /// <summary>Variante de los mensajes por frase en español: <c>true</c> = argentina, <c>false</c> = neutra.</summary>
    public event Action<bool>? PhraseDialectChanged;

    public event Action<bool>? StartFullscreenChanged;

    public event Action<bool>? AutoAdvanceChanged;

    public event Action<bool>? RemoteControlChanged;

    /// <summary>Volumen de la música de interfaz elegido (0–100 %); se aplica al instante.</summary>
    public event Action<int>? MusicVolumeChanged;

    /// <summary>Red elegida para el acceso de invitados (<c>null</c> = automática).</summary>
    public event Action<string?>? GuestInterfaceChanged;

    public event Action<bool>? TechnicalBarChanged;

    public event EventHandler? BackRequested;

    public OptionsView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => OnTick();
        PreviewKeyDown += OnKeyDown;
        TabsPanel.PreviewKeyDown += OnTabsKeyDown;
        ContentPanel.PreviewKeyDown += OnContentKeyDown;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    private string Lang => _model?.Language ?? "es";

    private string T(string key) => OptionsTexts.Get(Lang, key);

    /// <summary>Muestra (o refresca) la pantalla con el modelo dado, conservando la sección y el foco.</summary>
    public void Show(OptionsModel model, Section? section = null)
    {
        if (!ReferenceEquals(_attachedInput, model.Input))
        {
            if (_attachedInput is not null)
            {
                _attachedInput.Changed -= OnMicrophonesChanged;
                _attachedInput.Failed -= OnInputFailed;
            }

            _attachedInput = model.Input;
            model.Input.Changed += OnMicrophonesChanged;
            model.Input.Failed += OnInputFailed;
        }

        _model = model;
        if (section is { } wanted)
        {
            _section = wanted;
        }

        TitleText.Text = T("title");
        HintText.Text = T("hint");
        BackButton.Content = T("back");
        BuildTabs();
        BuildContent();
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    // ---- Secciones ----------------------------------------------------------------------------------------

    private void BuildTabs()
    {
        TabsPanel.Children.Clear();
        var names = new[] { "tab.audio", "tab.library", "tab.game", "tab.screen", "tab.help", "tab.credits" };
        for (var i = 0; i < names.Length; i++)
        {
            var section = (Section)i;
            var button = new Button
            {
                Content = T(names[i]),
                FontSize = 24,
                Margin = new Thickness(0, 0, 10, 0),
                MinWidth = 160,
                Foreground = section == _section ? (Brush)FindResource("GoldBrush") : (Brush)FindResource("TextBrush"),
                Tag = $"tab{i}",
            };
            button.SetResourceReference(StyleProperty, "PixelButton");
            button.Click += (_, _) =>
            {
                _section = section;
                _focusKey = null;
                BuildTabs();
                BuildContent();
            };
            TabsPanel.Children.Add(button);
        }
    }

    private void BuildContent()
    {
        ContentPanel.Children.Clear();
        _micRows.Clear();
        _micsHost = null;
        _createButton = null;
        _status = null;
        if (_model is null)
        {
            return;
        }

        switch (_section)
        {
            case Section.Audio:
                BuildAudio(_model);
                break;
            case Section.Library:
                BuildLibrary(_model);
                break;
            case Section.Game:
                BuildGame(_model);
                break;
            case Section.Screen:
                BuildScreen(_model);
                break;
            case Section.Credits:
                BuildCredits();
                break;
            default:
                BuildHelp();
                break;
        }

        ContentScroll.ScrollToTop();
        Dispatcher.BeginInvoke(RestoreFocus, DispatcherPriority.Input);
    }

    private void RestoreFocus()
    {
        if (_focusKey is not null && FindByTag(ContentPanel, _focusKey) is { } target)
        {
            target.Focus();
            return;
        }

        // Sin un renglón recordado el foco queda en la pestaña activa: ← → cambian de sección, ↓ entra.
        if (!FocusActiveTab())
        {
            FindFirstFocusable(ContentPanel)?.Focus();
        }
    }

    private bool FocusActiveTab()
    {
        if (FindByTag(TabsPanel, $"tab{(int)_section}") is { } tab)
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
                BuildTabs();
                BuildContent();
                e.Handled = true;
                break;
            case Key.Down or Key.Enter:
                if (_section is Section.Help or Section.Credits)
                {
                    ContentScroll.ScrollToVerticalOffset(ContentScroll.VerticalOffset + 60); // la guía no tiene controles
                }
                else
                {
                    FindFirstFocusable(ContentPanel)?.Focus();
                }

                e.Handled = true;
                break;
            case Key.Up when _section is Section.Help or Section.Credits:
                ContentScroll.ScrollToVerticalOffset(ContentScroll.VerticalOffset - 60);
                e.Handled = true;
                break;
        }
    }

    /// <summary>↑ desde la primera opción vuelve a las pestañas.</summary>
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
            if (child is Button { IsEnabled: true, Focusable: true } button)
            {
                return button;
            }

            if (FindFirstFocusable(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (ChoiceButton.AnyOpen)
        {
            return; // hay un select desplegado: Esc y las flechas son suyos
        }

        switch (e.Key)
        {
            case Key.Escape:
                BackRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
            case Key.PageDown or Key.PageUp:
                var count = Enum.GetValues<Section>().Length;
                var next = ((int)_section + (e.Key == Key.PageDown ? 1 : count - 1)) % count;
                _section = (Section)next;
                _focusKey = null;
                BuildTabs();
                BuildContent();
                e.Handled = true;
                break;
        }
    }

    // ---- Piezas de interfaz -------------------------------------------------------------------------------

    private Grid Row(string label, FrameworkElement control, double controlWidth = 440)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = label,
            FontFamily = (FontFamily)FindResource("PixelFont"),
            FontSize = 28,
            Foreground = (Brush)FindResource("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        control.Width = controlWidth;
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    private ChoiceButton Choice(string key, IReadOnlyList<string> items, int index, Action<int> onChanged)
    {
        var choice = new ChoiceButton { Tag = key };
        choice.Set(items, index);
        choice.GotKeyboardFocus += (_, _) => _focusKey = key;
        choice.Changed += (_, _) => onChanged(choice.SelectedIndex);
        return choice;
    }

    private Button ActionButton(string key, string text, Action onClick, double fontSize = 22)
    {
        var button = new Button { Content = text, FontSize = fontSize, Tag = key };
        button.SetResourceReference(StyleProperty, "PixelButton");
        button.GotKeyboardFocus += (_, _) => _focusKey = key;
        button.Click += (_, _) => onClick();
        return button;
    }

    private TextBlock Note(string text, double fontSize = 15) => new()
    {
        Text = text,
        FontFamily = (FontFamily)FindResource("BodyFont"),
        FontSize = fontSize,
        Foreground = (Brush)FindResource("DimBrush"),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 1060,
        Margin = new Thickness(0, 4, 0, 4),
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private TextBlock Heading(string text) => new()
    {
        Text = text,
        FontFamily = (FontFamily)FindResource("PixelFont"),
        FontSize = 32,
        Foreground = (Brush)FindResource("GoldBrush"),
        Margin = new Thickness(0, 18, 0, 6),
    };

    // ---- Audio --------------------------------------------------------------------------------------------

    private void BuildAudio(OptionsModel m)
    {
        var engineItems = new[]
        {
            T("audio.engine.windows"),
            m.Asio.IsAvailable ? T("audio.engine.asio") : T("audio.engine.asio.na"),
        };
        ContentPanel.Children.Add(Row(T("audio.engine"), Choice("engine", engineItems, (int)m.Engine.Kind, i =>
        {
            var kind = (AudioEngineKind)i;
            if (kind == AudioEngineKind.Asio && !m.Asio.IsAvailable)
            {
                BuildContent(); // ASIO no está: vuelve a la opción activa y la nota explica por qué
                return;
            }

            if (kind != m.Engine.Kind)
            {
                EngineRequested?.Invoke(kind);
            }
        })));

        if (m.Engine.Kind == AudioEngineKind.Asio && m.Asio.Drivers.Count > 1)
        {
            var index = Math.Max(0, m.Asio.Drivers.ToList()
                .FindIndex(d => string.Equals(d, m.PreferredDriver, StringComparison.OrdinalIgnoreCase)));
            ContentPanel.Children.Add(Row(T("audio.driver"), Choice("driver", m.Asio.Drivers, index, i =>
            {
                if (!string.Equals(m.Asio.Drivers[i], m.PreferredDriver, StringComparison.OrdinalIgnoreCase))
                {
                    AsioDriverRequested?.Invoke(m.Asio.Drivers[i]);
                }
            })));
        }

        if (m.Engine.OutputPairs.Count > 0)
        {
            ContentPanel.Children.Add(Row(T("audio.output"), Choice("output", m.Engine.OutputPairs, m.Engine.OutputPair, i =>
            {
                if (i != m.Engine.OutputPair)
                {
                    OutputPairRequested?.Invoke(i);
                }
            })));
        }

        ContentPanel.Children.Add(Note(DescribeEngine(m)));

        ContentPanel.Children.Add(Row(T("audio.music"),
            Choice("music", Kantor.Core.Music.MusicVolume.Steps.Select(p => $"{p} %").ToList(),
                Kantor.Core.Music.MusicVolume.IndexOf(m.MusicVolume),
                i => MusicVolumeChanged?.Invoke(Kantor.Core.Music.MusicVolume.Steps[i]))));
        ContentPanel.Children.Add(Note(T("audio.music.help"), 18));

        try
        {
            _devices = m.Engine.ListInputs();
        }
        catch (System.Runtime.InteropServices.COMException e)
        {
            _devices = [];
            _status = Note(e.Message);
        }

        ContentPanel.Children.Add(Heading(T("audio.mics")));
        ContentPanel.Children.Add(Note(T("audio.mics.help")));
        if (_devices.Count == 0)
        {
            ContentPanel.Children.Add(Note(T("audio.nodevices"), 18));
        }

        _micsHost = new StackPanel();
        ContentPanel.Children.Add(_micsHost);

        _createButton = ActionButton("create", T("audio.mic.create"), CreateMicrophone);
        _createButton.HorizontalAlignment = HorizontalAlignment.Left;
        _createButton.Margin = new Thickness(0, 10, 0, 6);
        ContentPanel.Children.Add(_createButton);

        _status ??= Note(string.Empty);
        ContentPanel.Children.Add(_status);
        BuildMics();
    }

    private string DescribeEngine(OptionsModel m)
    {
        if (m.Engine.Kind == AudioEngineKind.Asio)
        {
            var latency = m.Engine is AsioEngine asio
                ? $" El controlador informa {asio.ReportedLatencies.Input * 1000.0 / AsioEngine.SampleRate:0.#} ms de entrada y " +
                  $"{asio.ReportedLatencies.Output * 1000.0 / AsioEngine.SampleRate:0.#} ms de salida."
                : string.Empty;
            return "ASIO: latencia baja, con entrada y salida directas del controlador (se usa la salida elegida arriba)." +
                   latency + " Las demás aplicaciones de Windows siguen pudiendo usar la interfaz. El retardo de voz de " +
                   "ASIO se calibra aparte (botón Calibrar de cada micrófono).";
        }

        if (m.Requested == AudioEngineKind.Asio)
        {
            return "Se pidió ASIO pero no se pudo usar; funciona Windows (compartido). " +
                   (m.EngineNote ?? m.Asio.UnavailableReason ?? "Revisá el controlador ASIO y volvé a elegirlo.");
        }

        return m.Asio.IsAvailable
            ? $"Windows (compartido): funciona con cualquier dispositivo; usa la salida predeterminada de Windows y " +
              $"tiene más latencia. ASIO está disponible ({string.Join(", ", m.Asio.Drivers)})."
            : "Windows (compartido): funciona con cualquier dispositivo; usa la salida predeterminada de Windows. " +
              (m.Asio.UnavailableReason ?? "ASIO no está disponible.");
    }

    /// <summary>Primer canal del dispositivo que ningún otro micrófono usa, o −1.</summary>
    private int FirstFreeChannel(AudioInputDevice device, int exceptSlot)
    {
        for (var c = 0; c < device.Channels; c++)
        {
            var owner = _model!.Input.Map.Find(device.Id, c);
            if (owner < 0 || owner == exceptSlot)
            {
                return c;
            }
        }

        return -1;
    }

    /// <summary>
    /// Crea un micrófono en el primer puesto libre, sobre el primer canal que nadie use. Después se le cambia el
    /// dispositivo y el canal.
    /// </summary>
    private void CreateMicrophone()
    {
        if (_model is null)
        {
            return;
        }

        var slot = Enumerable.Range(0, MicrophoneMap.MaxSlots).FirstOrDefault(s => _model.Input.Map[s] is null, -1);
        if (slot < 0)
        {
            return;
        }

        foreach (var device in _devices)
        {
            var channel = FirstFreeChannel(device, exceptSlot: -1);
            if (channel >= 0)
            {
                AssignMicrophone(slot, device, channel);
                return;
            }
        }

        SetStatus(T("audio.mic.nochannel"));
    }

    private void AssignMicrophone(int slot, AudioInputDevice device, int channel)
    {
        if (_model is null)
        {
            return;
        }

        try
        {
            _model.Input.Assign(slot, device, channel);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException
                                      or UnauthorizedAccessException)
        {
            SetStatus(e.Message);
            BuildMics();
        }
    }

    /// <summary>Cambió la asignación de micrófonos (desde esta pantalla o desde otra parte): se vuelven a mostrar.</summary>
    private void OnMicrophonesChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_model is not null && _section == Section.Audio && _micsHost is not null)
        {
            BuildMics();
        }
    });

    private void OnInputFailed(object? sender, Exception? error) => Dispatcher.BeginInvoke(() =>
        SetStatus($"{error?.Message ?? "dispositivo no disponible"}"));

    private void SetStatus(string text)
    {
        if (_status is not null)
        {
            _status.Text = text;
        }
    }

    private void BuildMics()
    {
        if (_micsHost is null || _model is null)
        {
            return;
        }

        _micsHost.Children.Clear();
        _micRows.Clear();
        var slots = _model.Input.Map.ActiveSlots;
        if (slots.Count == 0)
        {
            _micsHost.Children.Add(Note(T("audio.nomics"), 18));
        }

        foreach (var slot in slots)
        {
            _micsHost.Children.Add(BuildMicBlock(_model, slot));
        }

        if (_createButton is not null)
        {
            _createButton.IsEnabled = slots.Count < MicrophoneMap.MaxSlots && _devices.Count > 0;
        }
    }

    /// <summary>
    /// Un micrófono: su nombre, el dispositivo y el canal (que se cambian ahí mismo), y debajo el nivel, la nota
    /// que detecta, su retardo y la calibración.
    /// </summary>
    private FrameworkElement BuildMicBlock(OptionsModel m, int slot)
    {
        var source = m.Input.Map[slot]!.Value;
        var color = PlayerColors.For(slot + 1);
        var current = m.Input.DeviceOf(slot) ?? _devices.FirstOrDefault(d => d.Id == source.DeviceId);

        var chip = new Border
        {
            Background = new SolidColorBrush(color),
            Width = 52,
            Height = 52,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = $"M{slot + 1}",
                FontFamily = (FontFamily)FindResource("PixelFont"),
                FontSize = 30,
                Foreground = (Brush)FindResource("InkBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        // dispositivo y canal
        var deviceItems = _devices.Select(d => d.Name).ToList();
        var deviceIndex = Math.Max(0, _devices.ToList().FindIndex(d => d.Id == source.DeviceId));
        var deviceChoice = Choice($"dev{slot}", deviceItems.Count > 0 ? deviceItems : [source.DeviceId], deviceIndex, i =>
        {
            if (i < 0 || i >= _devices.Count || _devices[i].Id == source.DeviceId)
            {
                return;
            }

            var channel = FirstFreeChannel(_devices[i], slot);
            if (channel < 0)
            {
                SetStatus(T("audio.mic.nochannel"));
                BuildMics();
                return;
            }

            AssignMicrophone(slot, _devices[i], channel);
        });
        deviceChoice.Width = 470;
        deviceChoice.FontSize = 20;

        var channelItems = current is null
            ? [$"{source.Channel + 1}"]
            : Enumerable.Range(0, current.Channels).Select(current.ChannelLabel).ToList();
        var channelChoice = Choice($"ch{slot}", channelItems, Math.Min(source.Channel, channelItems.Count - 1), i =>
        {
            if (current is null)
            {
                return;
            }

            var owner = m.Input.Map.Find(current.Id, i);
            if (owner >= 0 && owner != slot)
            {
                SetStatus(string.Format(T("audio.mic.inuse"), owner + 1));
                BuildMics(); // vuelve al canal que tenía
                return;
            }

            AssignMicrophone(slot, current, i);
        });
        channelChoice.Width = 250;
        channelChoice.FontSize = 20;

        var remove = ActionButton($"rm{slot}", T("audio.mic.remove"), () =>
        {
            try
            {
                m.Input.Unassign(slot);
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                SetStatus(e.Message);
            }
        }, 20);
        remove.Margin = new Thickness(10, 0, 0, 0);

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(deviceChoice);
        top.Children.Add(channelChoice);
        top.Children.Add(remove);

        // nivel, nota, retardo y calibración
        var meter = new PixelMeter { Width = 240, VerticalAlignment = VerticalAlignment.Center };
        var level = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("BodyFont"),
            FontSize = 14,
            Foreground = (Brush)FindResource("DimBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Width = 150,
        };
        var note = new TextBlock
        {
            Text = "—",
            FontFamily = (FontFamily)FindResource("PixelFont"),
            FontSize = 32,
            Foreground = (Brush)FindResource("TextBrush"),
            Width = 80,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var detail = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("BodyFont"),
            FontSize = 14,
            Foreground = (Brush)FindResource("DimBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 190,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var delays = Enumerable.Range(0, UserSettings.MaxVoiceDelayMs / UserSettings.VoiceDelayStepMs + 1)
            .Select(i => $"{i * UserSettings.VoiceDelayStepMs} ms").ToList();
        var delayIndex = Math.Clamp(m.DelayMs(slot) / UserSettings.VoiceDelayStepMs, 0, delays.Count - 1);
        var delay = Choice($"delay{slot}", delays, delayIndex, i =>
            DelayChanged?.Invoke(slot, i * UserSettings.VoiceDelayStepMs));
        delay.Width = 170;
        delay.FontSize = 20;
        delay.Margin = new Thickness(8, 0, 0, 0);
        delay.ToolTip = T("audio.delay");
        var calibrate = ActionButton($"cal{slot}", T("audio.calibrate"), () => CalibrateRequested?.Invoke(slot), 20);
        calibrate.Margin = new Thickness(8, 0, 0, 0);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(meter);
        bottom.Children.Add(level);
        bottom.Children.Add(note);
        bottom.Children.Add(detail);
        bottom.Children.Add(delay);
        bottom.Children.Add(calibrate);

        var lines = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        lines.Children.Add(top);
        lines.Children.Add(bottom);

        var block = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        block.Children.Add(chip);
        block.Children.Add(lines);
        _micRows.Add(new MicRow(slot, meter, level, note, detail));
        return block;
    }

    // ---- Actualización en vivo ----------------------------------------------------------------------------

    private void OnTick()
    {
        if (_model is null || _section != Section.Audio)
        {
            return;
        }

        var now = _clock.Elapsed;
        foreach (var row in _micRows)
        {
            if (_model.Input.SessionOf(row.Slot) is not { } session || _model.Input.Map[row.Slot] is not { } source)
            {
                continue;
            }

            var reading = session.Read(source.Channel);
            var rms = reading.Level.RmsDb;
            row.Meter.Value = (Math.Clamp(rms, MinDb, 0) - MinDb) / -MinDb;
            row.Level.Text = rms <= LevelReading.FloorDb + 1
                ? T("audio.silence")
                : $"{rms:0.0} dB   pico {reading.Level.PeakDb:0.0}";
            row.Level.Foreground = reading.Level.PeakDb > -1 ? Brushes.IndianRed : (Brush)FindResource("DimBrush");

            if (reading.Pitch is { } frame)
            {
                row.Last = frame;
                if (frame.IsVoiced)
                {
                    row.LastVoiced = now;
                }
            }

            ShowPitch(row, now);
        }
    }

    private void ShowPitch(MicRow row, TimeSpan now)
    {
        // Se mantiene la última nota un instante para que la lectura no parpadee entre cuadros.
        var frame = row.Last;
        if (!frame.IsVoiced || now - row.LastVoiced > VoiceHold)
        {
            row.Note.Text = "—";
            row.Detail.Text = frame.LevelDb <= PitchTracker.DefaultGateDb
                ? T("audio.nosignal")
                : frame.LevelDb > LevelReading.FloorDb ? $"{T("audio.novoice")} · {frame.LevelDb:0.0} dB" : T("audio.novoice");
            return;
        }

        var pitch = MusicalPitch.FromFrequency(frame.Frequency);
        row.Note.Text = pitch.Name;
        row.Detail.Text = $"{frame.Frequency:0.0} Hz · {pitch.Cents:+0;-0;0} cent · {frame.Confidence:P0}";
    }

    // ---- Biblioteca ---------------------------------------------------------------------------------------

    private void BuildLibrary(OptionsModel m)
    {
        ContentPanel.Children.Add(Heading(T("lib.folders")));
        if (m.Folders.Count == 0)
        {
            ContentPanel.Children.Add(Note(T("lib.none"), 18));
        }

        for (var i = 0; i < m.Folders.Count; i++)
        {
            var path = m.Folders[i];
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock
            {
                Text = path,
                FontFamily = (FontFamily)FindResource("BodyFont"),
                FontSize = 18,
                Foreground = (Brush)FindResource("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var remove = ActionButton($"rm{i}", T("lib.remove"), () => RemoveFolderRequested?.Invoke(path), 20);
            Grid.SetColumn(remove, 1);
            row.Children.Add(text);
            row.Children.Add(remove);
            ContentPanel.Children.Add(row);
        }

        var add = ActionButton("add", T("lib.add"), () => AddFolderRequested?.Invoke());
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 10, 0, 18);
        ContentPanel.Children.Add(add);

        ContentPanel.Children.Add(Row(T("lib.view"),
            Choice("grid", [T("lib.view.list"), T("lib.view.grid")], m.Grid ? 1 : 0, i => GridChanged?.Invoke(i == 1))));
    }

    // ---- Juego --------------------------------------------------------------------------------------------

    private void BuildGame(OptionsModel m)
    {
        ContentPanel.Children.Add(Row(T("game.difficulty"),
            Choice("difficulty", [T("game.easy"), T("game.medium"), T("game.hard")], m.Difficulty,
                i => DifficultyChanged?.Invoke(i))));
        ContentPanel.Children.Add(Row(T("game.voice"),
            Choice("voice", [T("game.voice.beat"), T("game.voice.raw")], m.VoiceDisplay,
                i => VoiceDisplayChanged?.Invoke(i))));
        ContentPanel.Children.Add(Row(T("game.language"),
            Choice("language", ["Español", "English"], m.Language == "en" ? 1 : 0,
                i => LanguageChanged?.Invoke(i == 1 ? "en" : "es"))));

        var nearest = 0;
        for (var i = 1; i < NonsenseSteps.Length; i++)
        {
            if (Math.Abs(NonsenseSteps[i] - m.Nonsense) < Math.Abs(NonsenseSteps[nearest] - m.Nonsense))
            {
                nearest = i;
            }
        }

        ContentPanel.Children.Add(Row(T("game.nonsense"),
            Choice("nonsense", NonsenseSteps.Select(p => $"{p * 100:0} %").ToList(), nearest,
                i => NonsenseChanged?.Invoke(NonsenseSteps[i]))));
        ContentPanel.Children.Add(Row(T("game.preview"),
            Choice("preview", [T("no"), T("yes")], m.SongPreview ? 1 : 0, i => SongPreviewChanged?.Invoke(i == 1))));
        ContentPanel.Children.Add(Row(T("game.feedback"),
            Choice("feedback", [T("no"), T("yes")], m.PhraseFeedback ? 1 : 0, i => PhraseFeedbackChanged?.Invoke(i == 1))));
        ContentPanel.Children.Add(Row(T("game.dialect"),
            Choice("dialect", [T("game.dialect.neutral"), T("game.dialect.argentine")], m.ArgentinePhrases ? 1 : 0,
                i => PhraseDialectChanged?.Invoke(i == 1))));
        ContentPanel.Children.Add(Row(T("game.autoadvance"),
            Choice("autoadvance", [T("no"), T("yes")], m.AutoAdvance ? 1 : 0, i => AutoAdvanceChanged?.Invoke(i == 1))));
        ContentPanel.Children.Add(PixelUi.Note(T("game.autoadvance.help"), 18));
        ContentPanel.Children.Add(Row(T("game.remote"),
            Choice("remote", [T("game.remote.off"), T("yes")], m.RemoteControl ? 1 : 0,
                i => RemoteControlChanged?.Invoke(i == 1))));
        ContentPanel.Children.Add(PixelUi.Note(m.RemoteStatus ?? T("game.remote.help"), 18));

        if (m.GuestNetworks.Count > 0)
        {
            var guestIndex = Math.Max(0, m.GuestNetworks.ToList().FindIndex(n => n.Address == m.GuestInterface) + 1);
            ContentPanel.Children.Add(Row(GuestTexts.OptionLabel(m.Language),
                Choice("guestnet", [GuestTexts.OptionAuto(m.Language), .. m.GuestNetworks.Select(n => n.Label)], guestIndex,
                    i => GuestInterfaceChanged?.Invoke(i == 0 ? null : m.GuestNetworks[i - 1].Address))));
            ContentPanel.Children.Add(PixelUi.Note(GuestTexts.OptionHelp(m.Language), 18));
        }
    }

    // ---- Pantalla -----------------------------------------------------------------------------------------

    private void BuildScreen(OptionsModel m)
    {
        ContentPanel.Children.Add(Row(T("screen.fullscreen"),
            Choice("fullscreen", [T("no"), T("yes")], m.StartFullscreen ? 1 : 0,
                i => StartFullscreenChanged?.Invoke(i == 1))));
        ContentPanel.Children.Add(Row(T("screen.techbar"),
            Choice("techbar", [T("hidden"), T("visible")], m.TechnicalBar ? 1 : 0,
                i => TechnicalBarChanged?.Invoke(i == 1))));
    }

    // ---- Guía ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Texto de ayuda sin controles enfocables: el foco queda en la pestaña y ↑ ↓ no recorren nada; la rueda
    /// del mouse desplaza el contenido.
    /// </summary>
    private void BuildHelp()
    {
        foreach (var topic in new[] { "songs", "mics", "latency", "play", "remote", "guest" })
        {
            ContentPanel.Children.Add(Heading(T($"help.{topic}.title")));
            ContentPanel.Children.Add(Note(T($"help.{topic}.body"), 20));
        }
    }

    /// <summary>Créditos: versión y edición, autoría, inspiración, marca y tecnología de terceros (texto sin controles).</summary>
    private void BuildCredits()
    {
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        var line = string.Format(T("credits.version"), version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}");
        if (Kantor.Core.Characters.CharacterEdition.IsStore)
        {
            line += " · " + T("credits.edition.store");
        }

        ContentPanel.Children.Add(Heading(T("credits.app.title")));
        ContentPanel.Children.Add(Note(line, 22));
        ContentPanel.Children.Add(Note(T("credits.app.body"), 20));
        foreach (var topic in new[] { "inspired", "art", "libs", "songs" })
        {
            ContentPanel.Children.Add(Heading(T($"credits.{topic}.title")));
            ContentPanel.Children.Add(Note(T($"credits.{topic}.body"), 20));
        }
    }

    private sealed class MicRow(int slot, PixelMeter meter, TextBlock level, TextBlock note, TextBlock detail)
    {
        public int Slot { get; } = slot;

        public PixelMeter Meter { get; } = meter;

        public TextBlock Level { get; } = level;

        public TextBlock Note { get; } = note;

        public TextBlock Detail { get; } = detail;

        public PitchFrame Last { get; set; } = PitchFrame.Silent;

        public TimeSpan LastVoiced { get; set; } = TimeSpan.MinValue;
    }
}
