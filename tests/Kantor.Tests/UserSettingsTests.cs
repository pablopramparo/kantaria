// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Scoring;
using Kantor.Core.Settings;

namespace Kantor.Tests;

public sealed class UserSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kantor-tests-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "sub", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Defaults_are_easy_by_beat_and_the_measured_delay()
    {
        var s = new UserSettings();

        Assert.Equal(Difficulty.Easy, s.Difficulty);
        Assert.Equal(0, s.VoiceDisplay);
        Assert.Equal(175, s.VoiceDelayMs);
        Assert.Null(s.InputDeviceId);
    }

    [Fact]
    public void Saved_settings_are_read_back_and_the_folder_is_created()
    {
        var saved = new UserSettings
        {
            Difficulty = Difficulty.Hard,
            VoiceDisplay = 1,
            VoiceDelayMs = 125,
            InputDeviceId = "{0.0.1.00000000}.{abc}",
            InputChannel = 1,
        };

        Assert.True(UserSettingsStore.Save(FilePath, saved));
        var loaded = UserSettingsStore.Load(FilePath);

        Assert.Equal(saved, loaded);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void A_missing_file_gives_defaults_with_the_system_language()
    {
        Assert.Equal(new UserSettings { CommentLanguage = Kantor.Core.Localization.AppLanguage.FromSystem() },
            UserSettingsStore.Load(FilePath));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"VoiceDelayMs\": \"abc\" }")]
    public void A_damaged_file_gives_defaults_instead_of_throwing(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, content);

        Assert.Equal(new UserSettings { CommentLanguage = Kantor.Core.Localization.AppLanguage.FromSystem() },
            UserSettingsStore.Load(FilePath));
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(0, 0)]
    [InlineData(12, 0)]
    [InlineData(13, 25)]
    [InlineData(175, 175)]
    [InlineData(180, 175)]
    [InlineData(300, 300)]
    [InlineData(5000, 300)]
    public void The_delay_is_clamped_to_the_selector_steps(int input, int expected)
    {
        Assert.Equal(expected, new UserSettings { VoiceDelayMs = input }.Normalized().VoiceDelayMs);
    }

    [Fact]
    public void Out_of_range_values_are_replaced_by_valid_ones()
    {
        var s = new UserSettings { Difficulty = (Difficulty)9, VoiceDisplay = 7, InputChannel = -3, InputDeviceId = "  " }.Normalized();

        Assert.Equal(Difficulty.Easy, s.Difficulty);
        Assert.Equal(0, s.VoiceDisplay);
        Assert.Equal(0, s.InputChannel);
        Assert.Null(s.InputDeviceId);
    }

    [Fact]
    public void Saving_over_an_existing_file_replaces_it()
    {
        UserSettingsStore.Save(FilePath, new UserSettings { VoiceDelayMs = 50 });
        UserSettingsStore.Save(FilePath, new UserSettings { VoiceDelayMs = 250 });

        Assert.Equal(250, UserSettingsStore.Load(FilePath).VoiceDelayMs);
    }
}

public class DelayProfileTests
{
    [Fact]
    public void A_profile_is_found_by_output_input_and_channel()
    {
        var key = UserSettings.ProfileKey("out", "in", 1);
        var s = new UserSettings().WithDelayFor(key, 175);

        Assert.Equal(175, s.DelayFor(key));
        Assert.Null(s.DelayFor(UserSettings.ProfileKey("out", "in", 0)));
        Assert.Null(s.DelayFor(UserSettings.ProfileKey("other", "in", 1)));
        Assert.Null(new UserSettings().DelayFor(key));
    }

    [Fact]
    public void Setting_a_profile_does_not_change_the_original()
    {
        var original = new UserSettings();
        var changed = original.WithDelayFor("k", 100);

        Assert.Null(original.DelayProfiles);
        Assert.Equal(100, changed.DelayFor("k"));
    }

    [Fact]
    public void Profiles_survive_saving_and_are_corrected_when_loaded()
    {
        var path = Path.Combine(Path.GetTempPath(), "kantor-tests-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var s = new UserSettings().WithDelayFor("a|b|0", 175).WithDelayFor("c|d|1", 9999).WithDelayFor("e|f|0", 12);
            Assert.True(UserSettingsStore.Save(path, s));

            var loaded = UserSettingsStore.Load(path);

            Assert.Equal(175, loaded.DelayFor("a|b|0"));
            Assert.Equal(300, loaded.DelayFor("c|d|1")); // se lleva al máximo del selector
            Assert.Equal(0, loaded.DelayFor("e|f|0"));    // y al paso más cercano
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void An_empty_profile_table_is_dropped()
    {
        var s = new UserSettings { DelayProfiles = new Dictionary<string, int>() }.Normalized();

        Assert.Null(s.DelayProfiles);
    }
}
