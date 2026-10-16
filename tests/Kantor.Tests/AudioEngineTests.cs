// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Audio;
using Kantor.Core.Settings;

namespace Kantor.Tests;

public class AudioEngineTests
{
    [Fact]
    public void Windows_is_used_when_asked_for_and_needs_no_explanation()
    {
        var r = AudioEngineChoice.Resolve(AudioEngineKind.Windows, asioAvailable: false);

        Assert.Equal(AudioEngineKind.Windows, r.Kind);
        Assert.Null(r.FallbackReason);
    }

    [Fact]
    public void Asio_is_used_when_requested_and_available()
    {
        var r = AudioEngineChoice.Resolve(AudioEngineKind.Asio, asioAvailable: true);

        Assert.Equal(AudioEngineKind.Asio, r.Kind);
        Assert.Null(r.FallbackReason);
    }

    [Fact]
    public void Asio_requested_but_unavailable_falls_back_to_windows_with_a_reason()
    {
        var generic = AudioEngineChoice.Resolve(AudioEngineKind.Asio, asioAvailable: false);
        var specific = AudioEngineChoice.Resolve(AudioEngineKind.Asio, asioAvailable: false, "No hay controlador ASIO.");

        Assert.Equal(AudioEngineKind.Windows, generic.Kind);
        Assert.False(string.IsNullOrWhiteSpace(generic.FallbackReason));
        Assert.Equal(AudioEngineKind.Windows, specific.Kind);
        Assert.Equal("No hay controlador ASIO.", specific.FallbackReason);
    }

    [Fact]
    public void The_engine_preference_is_global_and_persists()
    {
        var path = Path.Combine(Path.GetTempPath(), "kantor-tests-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            Assert.Equal(AudioEngineKind.Windows, new UserSettings().AudioEngine);

            UserSettingsStore.Save(path, new UserSettings { AudioEngine = AudioEngineKind.Asio });

            Assert.Equal(AudioEngineKind.Asio, UserSettingsStore.Load(path).AudioEngine);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void An_unknown_engine_value_in_the_file_falls_back_to_windows()
    {
        var path = Path.Combine(Path.GetTempPath(), "kantor-tests-" + Guid.NewGuid().ToString("N"), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "{ \"AudioEngine\": 7 }");

            Assert.Equal(AudioEngineKind.Windows, UserSettingsStore.Load(path).AudioEngine);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}

public class AsioSettingsTests
{
    [Fact]
    public void Asio_has_its_own_delay_that_starts_at_zero_and_never_inherits_windows()
    {
        var s = new UserSettings { VoiceDelayMs = 175 };

        Assert.Equal(0, s.AsioVoiceDelayMs);
        Assert.Equal(175, s.VoiceDelayMs);
    }

    [Fact]
    public void Profile_keys_never_collide_between_engines_outputs_and_channels()
    {
        var windows = UserSettings.ProfileKey("{out}", "{in}", 1);
        var asio = UserSettings.AsioProfileKey("Focusrite USB ASIO", 0, 1);

        Assert.NotEqual(windows, asio);
        Assert.NotEqual(asio, UserSettings.AsioProfileKey("Focusrite USB ASIO", 1, 1));   // otro par de salida
        Assert.NotEqual(asio, UserSettings.AsioProfileKey("Focusrite USB ASIO", 0, 0));   // otro canal de entrada
        Assert.NotEqual(asio, UserSettings.AsioProfileKey("Otro controlador", 0, 1));     // otro controlador
        Assert.StartsWith("asio|", asio);
    }

    [Fact]
    public void An_asio_calibration_does_not_change_the_windows_one()
    {
        var windowsKey = UserSettings.ProfileKey("{out}", "{in}", 0);
        var asioKey = UserSettings.AsioProfileKey("Focusrite USB ASIO", 0, 0);

        var s = new UserSettings().WithDelayFor(windowsKey, 175).WithDelayFor(asioKey, 0);

        Assert.Equal(175, s.DelayFor(windowsKey));
        Assert.Equal(0, s.DelayFor(asioKey));
    }

    [Fact]
    public void Asio_preferences_are_normalized_and_persisted()
    {
        var path = Path.Combine(Path.GetTempPath(), "kantor-tests-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var saved = new UserSettings
            {
                AudioEngine = AudioEngineKind.Asio,
                AsioDriver = "Focusrite USB ASIO",
                AsioOutputPair = 1,
                AsioVoiceDelayMs = 25,
            };
            UserSettingsStore.Save(path, saved);

            var loaded = UserSettingsStore.Load(path);
            Assert.Equal(AudioEngineKind.Asio, loaded.AudioEngine);
            Assert.Equal("Focusrite USB ASIO", loaded.AsioDriver);
            Assert.Equal(1, loaded.AsioOutputPair);
            Assert.Equal(25, loaded.AsioVoiceDelayMs);

            var bad = new UserSettings { AsioOutputPair = -4, AsioVoiceDelayMs = 7000, AsioDriver = " " }.Normalized();
            Assert.Equal(0, bad.AsioOutputPair);
            Assert.Equal(300, bad.AsioVoiceDelayMs);
            Assert.Null(bad.AsioDriver);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Channel_labels_use_driver_names_when_given()
    {
        var plain = new AudioInputDevice("id", "Micro", 2, 48000, SampleFormat.Float32, true);
        var named = plain with { ChannelNames = ["Input 1", "Loop 1"] };

        Assert.Equal("Canal 2", plain.ChannelLabel(1));
        Assert.Equal("Canal 2 · Loop 1", named.ChannelLabel(1));
        Assert.Equal("Canal 7", named.ChannelLabel(6));
    }
}
