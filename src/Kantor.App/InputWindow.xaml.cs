using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Kantor.App.Audio;
using Kantor.Core.Audio;
using Kantor.Core.Pitch;

namespace Kantor.App;

/// <summary>Elegir una entrada, ver el nivel de cada canal y comprobar qué nota se detecta. No hay monitorización.</summary>
public partial class InputWindow : Window
{
    private const double MinDb = -60;
    private const double ReleaseDbPerSecond = 40;
    private static readonly TimeSpan VoiceHold = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly List<ChannelRow> _rows = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private CaptureSession? _session;
    private int _pitchChannel;
    private TimeSpan _lastTick;
    private TimeSpan _lastVoiced = TimeSpan.MinValue;
    private PitchFrame _lastFrame = PitchFrame.Silent;

    public InputWindow()
    {
        InitializeComponent();
        _timer.Tick += OnTick;
        Loaded += (_, _) => RefreshDevices();
        Closed += (_, _) => StopSession();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshDevices();

    private void RefreshDevices()
    {
        var previous = (DeviceBox.SelectedItem as AudioInputDevice)?.Id;
        IReadOnlyList<AudioInputDevice> devices;
        try
        {
            devices = AudioInputCatalog.List();
        }
        catch (System.Runtime.InteropServices.COMException e)
        {
            StatusText.Text = $"No se pudo leer la lista de dispositivos: {e.Message}";
            return;
        }

        DeviceBox.ItemsSource = devices;
        DeviceBox.SelectedItem = devices.FirstOrDefault(d => d.Id == previous) ?? devices.FirstOrDefault();
        if (devices.Count == 0)
        {
            StopSession();
            ChannelsPanel.Children.Clear();
            StatusText.Text = "No hay dispositivos de entrada activos.";
        }
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceBox.SelectedItem is AudioInputDevice device)
        {
            StartSession(device);
        }
    }

    private void StartSession(AudioInputDevice device)
    {
        StopSession();
        BuildChannelRows(device);
        _pitchChannel = 0;
        _rows[0].Radio.IsChecked = true;
        _lastFrame = PitchFrame.Silent;
        _lastVoiced = TimeSpan.MinValue;

        try
        {
            var session = new CaptureSession(device);
            session.SetPitchChannels([_pitchChannel]);
            session.Stopped += OnSessionStopped;
            session.Start();
            _session = session;
            _lastTick = _clock.Elapsed;
            _timer.Start();
            StatusText.Text = $"Capturando: {device.Name} ({device.Channels} canal(es), {device.SampleRate} Hz).";
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException
                                      or UnauthorizedAccessException)
        {
            StatusText.Text = $"No se pudo abrir la entrada: {e.Message}";
        }
    }

    private void StopSession()
    {
        _timer.Stop();
        var session = _session;
        _session = null;
        if (session is not null)
        {
            session.Stopped -= OnSessionStopped;
            session.Dispose();
        }
    }

    private void OnSessionStopped(object? sender, Exception? error)
    {
        if (error is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            _timer.Stop();
            StatusText.Text = $"La entrada se detuvo: {error.Message}. Elegí otra o pulsá Actualizar.";
        });
    }

    private void BuildChannelRows(AudioInputDevice device)
    {
        ChannelsPanel.Children.Clear();
        _rows.Clear();
        for (var c = 0; c < device.Channels; c++)
        {
            var index = c;
            var radio = new RadioButton
            {
                Content = $"Canal {c + 1}",
                GroupName = "pitch",
                Width = 100,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.WhiteSmoke,
            };
            radio.Checked += (_, _) => SelectPitchChannel(index);

            var bar = new ProgressBar
            {
                Minimum = MinDb,
                Maximum = 0,
                Height = 16,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0xE0, 0x8E)),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x36)),
                BorderThickness = new Thickness(0),
            };

            var text = new TextBlock
            {
                Width = 150,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0x9E)),
            };

            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(text, 2);
            grid.Children.Add(radio);
            grid.Children.Add(bar);
            grid.Children.Add(text);
            ChannelsPanel.Children.Add(grid);
            _rows.Add(new ChannelRow(radio, bar, text));
        }
    }

    private void SelectPitchChannel(int channel)
    {
        _pitchChannel = channel;
        _session?.SetPitchChannels([channel]);
        _lastFrame = PitchFrame.Silent;
        _lastVoiced = TimeSpan.MinValue;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        var now = _clock.Elapsed;
        var dt = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        for (var c = 0; c < _rows.Count; c++)
        {
            var row = _rows[c];
            var level = session.Read(c).Level;
            var target = Math.Clamp(level.RmsDb, MinDb, 0);
            row.Bar.Value = Math.Max(target, row.Bar.Value - ReleaseDbPerSecond * dt);
            row.Text.Text = level.RmsDb <= LevelReading.FloorDb + 1
                ? "silencio"
                : $"{level.RmsDb:0.0} dB   pico {level.PeakDb:0.0}";
            row.Text.Foreground = level.PeakDb > -1
                ? Brushes.IndianRed
                : new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0x9E));
        }

        if (session.Read(_pitchChannel).Pitch is { } frame)
        {
            _lastFrame = frame;
            if (frame.IsVoiced)
            {
                _lastVoiced = now;
            }
        }

        ShowPitch(now);
    }

    private void ShowPitch(TimeSpan now)
    {
        // Se mantiene la última nota un instante para que la lectura no parpadee entre cuadros.
        if (!_lastFrame.IsVoiced || now - _lastVoiced > VoiceHold)
        {
            NoteText.Text = "—";
            FrequencyText.Text = _lastFrame.LevelDb <= PitchTracker.DefaultGateDb
                ? "Sin señal"
                : "Sin voz detectada";
            DetailText.Text = _lastFrame.LevelDb > LevelReading.FloorDb
                ? $"Nivel {_lastFrame.LevelDb:0.0} dB"
                : string.Empty;
            return;
        }

        var pitch = MusicalPitch.FromFrequency(_lastFrame.Frequency);
        NoteText.Text = pitch.Name;
        FrequencyText.Text = $"{_lastFrame.Frequency:0.0} Hz";
        DetailText.Text = $"{pitch.Cents:+0;-0;0} centésimas · tono UltraStar {pitch.UltraStarTone} · confianza {_lastFrame.Confidence:P0}";
    }

    private sealed record ChannelRow(RadioButton Radio, ProgressBar Bar, TextBlock Text);
}
