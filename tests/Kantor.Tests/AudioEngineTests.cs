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
