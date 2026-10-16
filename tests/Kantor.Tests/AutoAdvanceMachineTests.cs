// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Remote;
using Xunit;

namespace Kantor.Tests;

public class AutoAdvanceMachineTests
{
    private static TimeSpan T(double seconds) => TimeSpan.FromSeconds(seconds);

    private static AutoAdvanceInput Results(long seq = 1, bool animating = false, long interaction = 0,
        bool enabled = true, string? block = null) =>
        new(OnResults: true, seq, animating, interaction, enabled, block);

    private static readonly AutoAdvanceInput Away = new(false, 0, false, 0, true, null);

    [Fact]
    public void Counts_ten_seconds_after_the_animation_ends_and_then_fires_once()
    {
        var machine = new AutoAdvanceMachine();

        Assert.Equal(AutoAdvanceKind.Idle, machine.Step(T(0), Results(animating: true)).Kind);
        Assert.Equal(AutoAdvanceKind.Idle, machine.Step(T(3), Results(animating: true)).Kind);

        var armed = machine.Step(T(4), Results()); // la animación terminó a los 4 s
        Assert.Equal(AutoAdvanceKind.Counting, armed.Kind);
        Assert.Equal(T(10), armed.Remaining);
        Assert.Equal(T(6), machine.Step(T(8), Results()).Remaining);

        Assert.Equal(AutoAdvanceKind.Fire, machine.Step(T(14), Results()).Kind);
        Assert.Equal(AutoAdvanceKind.Idle, machine.Step(T(15), Results()).Kind); // no dispara dos veces
    }

    [Fact]
    public void Skipping_the_animation_starts_the_count_immediately()
    {
        var machine = new AutoAdvanceMachine();
        machine.Step(T(0), Results(animating: true));

        var armed = machine.Step(T(1), Results(animating: false)); // Enter: terminó de golpe

        Assert.Equal(AutoAdvanceKind.Counting, armed.Kind);
        Assert.Equal(T(10), armed.Remaining);
    }

    [Fact]
    public void A_local_interaction_after_the_animation_cancels_and_it_is_not_rearmed()
    {
        var machine = new AutoAdvanceMachine();
        machine.Step(T(0), Results());
        Assert.Equal(AutoAdvanceKind.Counting, machine.Step(T(2), Results()).Kind);

        Assert.Equal(AutoAdvanceKind.Idle, machine.Step(T(3), Results(interaction: 1)).Kind);
        Assert.Equal(AutoAdvanceKind.Idle, machine.Step(T(30), Results(interaction: 1)).Kind);
        Assert.False(machine.Armed);
    }

    [Fact]
    public void Turning_the_setting_off_cancels_an_active_count()
    {
        var machine = new AutoAdvanceMachine();
        machine.Step(T(0), Results());
        Assert.True(machine.Armed);

        Assert.Equal(AutoAdvanceKind.Idle, machine.Step(T(2), Results(enabled: false)).Kind);
        Assert.Equal(AutoAdvanceKind.Idle, machine.Step(T(3), Results(enabled: true)).Kind); // y no revive
    }

    [Fact]
    public void Enabling_the_setting_with_results_already_on_screen_does_not_arm_them()
    {
        var machine = new AutoAdvanceMachine();
        machine.Step(T(0), Results(enabled: false));

        Assert.Equal(AutoAdvanceKind.Idle, machine.Step(T(5), Results(enabled: true)).Kind);
        Assert.False(machine.Armed);

        // los resultados de la canción siguiente sí
        machine.Step(T(10), Away);
        Assert.Equal(AutoAdvanceKind.Counting, machine.Step(T(11), Results(seq: 2)).Kind);
    }

    [Fact]
    public void Explicit_cancel_stops_the_count_for_those_results_only()
    {
        var machine = new AutoAdvanceMachine();
        machine.Step(T(0), Results());
        machine.Cancel();

        Assert.Equal(AutoAdvanceKind.Idle, machine.Step(T(1), Results()).Kind);
        Assert.Equal(AutoAdvanceKind.Counting, machine.Step(T(2), Results(seq: 2)).Kind);
    }

    [Fact]
    public void Leaving_the_results_screen_cancels_the_count()
    {
        var machine = new AutoAdvanceMachine();
        machine.Step(T(0), Results());

        Assert.Equal(AutoAdvanceKind.Idle, machine.Step(T(2), Away).Kind);
        Assert.False(machine.Armed);
    }

    [Fact]
    public void It_never_advances_without_a_valid_next_song_and_singers()
    {
        var machine = new AutoAdvanceMachine();

        var blocked = machine.Step(T(0), Results(block: "no-singers"));
        Assert.Equal(AutoAdvanceKind.Blocked, blocked.Kind);
        Assert.Equal("no-singers", blocked.Reason);
        Assert.False(machine.Armed);
        Assert.Equal(AutoAdvanceKind.Idle, machine.Step(T(20), Results(block: null)).Kind); // no se arma después
    }

    [Fact]
    public void A_count_that_becomes_invalid_is_cancelled_and_reported()
    {
        var machine = new AutoAdvanceMachine();
        machine.Step(T(0), Results());
        Assert.Equal(AutoAdvanceKind.Counting, machine.Step(T(3), Results()).Kind);

        var blocked = machine.Step(T(4), Results(block: "invalid-singer")); // se archivó un jugador

        Assert.Equal(AutoAdvanceKind.Blocked, blocked.Kind);
        Assert.Equal(AutoAdvanceKind.Idle, machine.Step(T(30), Results()).Kind); // jamás dispara
    }
}
