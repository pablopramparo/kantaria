// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using System.Windows;
using Kantor.App.Audio;
using Kantor.Core.Audio;
using Kantor.Core.Calibration;

namespace Kantor.App;

/// <summary>Mide la latencia de audio del equipo y ofrece aplicarla como retardo de voz.</summary>
public partial class CalibrationWindow : Window
{
    private readonly AudioInputDevice _device;
    private readonly int _channel;
    private readonly Action<int> _apply;
    private int _suggested;

    public CalibrationWindow(AudioInputDevice device, int channel, Action<int> apply)
    {
        InitializeComponent();
        _device = device;
        _channel = channel;
        _apply = apply;
        DeviceText.Text = $"Entrada: {device.Name} · canal {channel + 1}";
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        ResultText.Text = string.Empty;
        StatusText.Text = "Reproduciendo y escuchando… no hables ni hagas ruido.";

        try
        {
            var result = await Task.Run(() => CalibrationRunner.Run(_device, _channel));
            Show(result);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            StatusText.Text = $"No se pudo hacer la prueba: {ex.Message}";
        }
        finally
        {
            StartButton.IsEnabled = true;
            StartButton.Content = "Repetir prueba";
        }
    }

    private void Show(CalibrationResult result)
    {
        if (!result.Success)
        {
            StatusText.Text = result.Problem ?? "No se pudo medir.";
            ResultText.Text = result.Accepted > 0
                ? $"Barridos claros: {result.Accepted} de {result.Expected}"
                : string.Empty;
            return;
        }

        _suggested = result.SuggestedDelayMs;
        StatusText.Text = $"Latencia medida: {result.MedianMs:0} ms (± {result.SpreadMs:0.#} ms), " +
                          $"{result.Accepted} de {result.Expected} barridos claros.";
        ResultText.Text = $"Retardo de voz sugerido: {result.SuggestedDelayMs} ms";
        ApplyButton.IsEnabled = true;
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        _apply(_suggested);
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
