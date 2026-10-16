// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Audio;
using Kantor.Core.Settings;
using Xunit;

namespace Kantor.Tests;

public class MicrophoneMapTests
{
    [Fact]
    public void Empty_map_has_no_active_slots()
    {
        var map = new MicrophoneMap();

        Assert.Empty(map.ActiveSlots);
        Assert.Equal(0, map.Count);
        Assert.Null(map[0]);
        Assert.Equal(-1, map.Find("dev", 0));
    }

    [Fact]
    public void Assign_places_source_and_keeps_slot_order_with_gaps()
    {
        var map = new MicrophoneMap();
        map.Assign(2, new MicrophoneSource("dev", 1));
        map.Assign(0, new MicrophoneSource("dev", 0));

        Assert.Equal([0, 2], map.ActiveSlots);
        Assert.Equal(2, map.Find("dev", 1));
    }

    [Fact]
    public void A_channel_can_feed_only_one_slot()
    {
        var map = new MicrophoneMap();
        map.Assign(0, new MicrophoneSource("dev", 0));

        var lost = map.Assign(1, new MicrophoneSource("dev", 0));

        Assert.Equal(0, lost);
        Assert.Null(map[0]);
        Assert.Equal(1, map.Find("dev", 0));
    }

    [Fact]
    public void Reassigning_the_same_source_to_the_same_slot_changes_nothing()
    {
        var map = new MicrophoneMap();
        map.Assign(0, new MicrophoneSource("dev", 0));

        Assert.Equal(-1, map.Assign(0, new MicrophoneSource("dev", 0)));
        Assert.Equal(1, map.Count);
    }

    [Fact]
    public void Slot_moves_to_another_channel_freeing_the_old_one()
    {
        var map = new MicrophoneMap();
        map.Assign(0, new MicrophoneSource("dev", 0));
        map.Assign(0, new MicrophoneSource("dev", 1));

        Assert.Equal(-1, map.Find("dev", 0));
        Assert.Equal(0, map.Find("dev", 1));
        Assert.Equal([1], map.ChannelsOf("dev"));
    }

    [Fact]
    public void Channels_and_devices_are_grouped_by_device()
    {
        var map = new MicrophoneMap();
        map.Assign(0, new MicrophoneSource("a", 0));
        map.Assign(1, new MicrophoneSource("a", 1));
        map.Assign(2, new MicrophoneSource("b", 0));

        Assert.Equal([0, 1], map.ChannelsOf("a").OrderBy(c => c));
        Assert.Equal([0], map.ChannelsOf("b"));
        Assert.Empty(map.ChannelsOf("c"));
        Assert.Equal(["a", "b"], map.DeviceIds().OrderBy(d => d));
    }

    [Fact]
    public void Unassign_empties_the_slot_once()
    {
        var map = new MicrophoneMap();
        map.Assign(1, new MicrophoneSource("dev", 0));

        Assert.True(map.Unassign(1));
        Assert.False(map.Unassign(1));
        Assert.Equal(0, map.Count);
    }

    [Fact]
    public void Assigning_outside_the_slot_range_is_rejected()
    {
        var map = new MicrophoneMap();

        Assert.Throws<ArgumentOutOfRangeException>(() => map.Assign(MicrophoneMap.MaxSlots, new MicrophoneSource("d", 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Assign(-1, new MicrophoneSource("d", 0)));
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(3, 1, 0)]
    [InlineData(0, 2, 0)]
    [InlineData(1, 2, 1)]
    [InlineData(2, 2, 0)]
    [InlineData(3, 2, 1)]
    [InlineData(0, 0, 0)]
    public void Voice_assignment_alternates_in_duets_and_is_shared_otherwise(int slot, int voices, int expected) =>
        Assert.Equal(expected, VoiceAssignment.VoiceFor(slot, voices));
}

public class MicrophoneSettingsTests
{
    [Fact]
    public void Legacy_single_input_migrates_to_slot_zero()
    {
        var s = new UserSettings { InputDeviceId = "dev", InputChannel = 1 };

        var mics = s.EffectiveMicrophones();

        Assert.Equal([new MicrophoneSetting(0, "dev", 1)], mics);
    }

    [Fact]
    public void Saved_microphones_win_over_the_legacy_input()
    {
        var s = new UserSettings
        {
            InputDeviceId = "old",
            Microphones = [new MicrophoneSetting(1, "new", 0)],
        };

        Assert.Equal([new MicrophoneSetting(1, "new", 0)], s.EffectiveMicrophones());
    }

    [Fact]
    public void Empty_saved_list_means_no_microphones_even_with_a_legacy_input()
    {
        var s = new UserSettings { InputDeviceId = "old", Microphones = [] };

        Assert.Empty(s.EffectiveMicrophones());
    }

    [Fact]
    public void Normalized_drops_invalid_duplicate_and_out_of_range_entries()
    {
        var s = new UserSettings
        {
            Microphones =
            [
                new MicrophoneSetting(0, "a", 0),
                new MicrophoneSetting(0, "b", 0),   // puesto repetido
                new MicrophoneSetting(1, "a", 0),   // canal repetido
                new MicrophoneSetting(7, "a", 1),   // puesto fuera de rango
                new MicrophoneSetting(2, "  ", 0),  // sin dispositivo
                new MicrophoneSetting(3, "b", -4),  // canal negativo: pasa a 0
            ],
        }.Normalized();

        Assert.Equal([new MicrophoneSetting(0, "a", 0), new MicrophoneSetting(3, "b", 0)], s.Microphones);
    }

    [Fact]
    public void Microphones_round_trip_through_the_settings_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kantor-mics-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new UserSettings
            {
                Microphones = [new MicrophoneSetting(0, "a", 0), new MicrophoneSetting(1, "a", 1)],
            };

            Assert.True(UserSettingsStore.Save(path, saved));
            var loaded = UserSettingsStore.Load(path);

            Assert.Equal(saved.Microphones, loaded.Microphones);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
