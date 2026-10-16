// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Characters;
using Kantor.Core.Data;
using Kantor.Core.Scoring;
using Xunit;

namespace Kantor.Tests;

/// <summary>Reloj que avanza un segundo por consulta: los instantes quedan ordenados y distintos.</summary>
internal sealed class TickingClock
{
    private DateTimeOffset _now = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

    public DateTimeOffset Next()
    {
        _now = _now.AddSeconds(1);
        return _now;
    }
}

public sealed class KantorStoreTests : IDisposable
{
    private readonly List<string> _files = [];
    private readonly TickingClock _clock = new();

    public void Dispose()
    {
        foreach (var file in _files)
        {
            foreach (var path in new[] { file, file + "-journal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private KantorStore Memory() => new(":memory:", _clock.Next);

    private string TempDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kantor-test-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    private static NewPerformance Perf(string id, params ResultInput[] results) =>
        new(id, SongIdentity.For("Artista", "Canción"), "Canción", "Artista", Difficulty: 0,
            ScoringRules.ScoringVersion, results);

    // ---- Esquema ------------------------------------------------------------------------------------------

    [Fact]
    public void New_database_is_created_with_the_current_schema_version()
    {
        using var store = Memory();

        Assert.Equal(KantorStore.SchemaVersion, store.CurrentSchemaVersion);
    }

    [Fact]
    public void Reopening_an_existing_database_keeps_data_and_schema_version()
    {
        var path = TempDb();
        long id;
        using (var first = new KantorStore(path, _clock.Next))
        {
            id = first.CreatePlayer("Ana").Id;
        }

        using var second = new KantorStore(path, _clock.Next);

        Assert.Equal(KantorStore.SchemaVersion, second.CurrentSchemaVersion);
        Assert.Equal("Ana", second.GetPlayer(id)!.Name);
    }

    // ---- Jugadores ----------------------------------------------------------------------------------------

    [Fact]
    public void Player_is_created_with_the_provisional_character_by_default()
    {
        using var store = Memory();

        var player = store.CreatePlayer("  Ana  ");

        Assert.Equal("Ana", player.Name);
        Assert.Equal(CharacterCatalog.Fallback.Id, player.CharacterId);
        Assert.False(player.Archived);
        Assert.Equal(player.CreatedAt, player.LastActivityAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("un nombre demasiado largo para entrar")]
    public void Invalid_names_are_rejected(string name)
    {
        using var store = Memory();

        Assert.Throws<StoreRuleException>(() => store.CreatePlayer(name));
    }

    [Fact]
    public void Active_names_are_unique_ignoring_case_but_archived_names_can_be_reused()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");

        Assert.Throws<StoreRuleException>(() => store.CreatePlayer("ANA"));

        store.ArchivePlayer(ana.Id);
        var again = store.CreatePlayer("ana");

        Assert.NotEqual(ana.Id, again.Id);
    }

    [Fact]
    public void Rename_rejects_a_name_already_in_use()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");
        store.CreatePlayer("Beto");

        Assert.Throws<StoreRuleException>(() => store.RenamePlayer(ana.Id, "beto"));
        store.RenamePlayer(ana.Id, "Anita");

        Assert.Equal("Anita", store.GetPlayer(ana.Id)!.Name);
    }

    [Fact]
    public void Unknown_character_id_is_stored_untouched_and_falls_back_only_when_shown()
    {
        using var store = Memory();
        var player = store.CreatePlayer("Ana");

        store.SetCharacter(player.Id, "robot-que-ya-no-existe");
        var stored = store.GetPlayer(player.Id)!;

        Assert.Equal("robot-que-ya-no-existe", stored.CharacterId);
        Assert.Equal(CharacterCatalog.Fallback, CharacterCatalog.Default.GetOrDefault(stored.CharacterId));
        Assert.Equal("robot-que-ya-no-existe", store.GetPlayer(player.Id)!.CharacterId);
    }

    [Fact]
    public void Archived_players_are_hidden_from_the_default_list_only()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");
        store.CreatePlayer("Beto");

        store.ArchivePlayer(ana.Id);

        Assert.Equal(["Beto"], store.ListPlayers().Select(p => p.Name));
        Assert.Equal(["Ana", "Beto"], store.ListPlayers(includeArchived: true).Select(p => p.Name));
        Assert.True(store.GetPlayer(ana.Id)!.Archived);
    }

    // ---- Sesiones -----------------------------------------------------------------------------------------

    [Fact]
    public void Only_one_session_can_be_open_and_starting_another_closes_the_previous()
    {
        using var store = Memory();
        var first = store.StartSession("Viernes");

        var second = store.StartSession("Sábado");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(second.Id, store.GetOpenSession()!.Id);
        var sessions = store.ListSessions();
        Assert.Single(sessions, s => s.IsOpen);
        var old = sessions.Single(s => s.Id == first.Id);
        Assert.False(old.IsOpen);
        Assert.NotNull(old.ClosedAt);
        Assert.True(old.ClosedAt >= old.StartedAt);
    }

    [Fact]
    public void Closing_is_explicit_and_idempotent()
    {
        using var store = Memory();
        var session = store.StartSession();

        Assert.True(store.CloseSession(session.Id));
        Assert.False(store.CloseSession(session.Id));
        Assert.Null(store.GetOpenSession());
    }

    [Fact]
    public void Selection_keeps_slots_and_makes_chosen_players_participants()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");
        var beto = store.CreatePlayer("Beto");
        var session = store.StartSession();
        store.AddPlayerToSession(session.Id, ana.Id);
        store.AddPlayerToSession(session.Id, beto.Id);

        store.SetSelection(session.Id, [new SelectionEntry(1, ana.Id), new SelectionEntry(3, beto.Id)]);

        Assert.Equal([new SelectionEntry(1, ana.Id), new SelectionEntry(3, beto.Id)], store.GetSelection(session.Id));
        Assert.Equal(["Ana", "Beto"], store.SessionPlayers(session.Id).Select(p => p.Name));
    }

    [Fact]
    public void A_session_can_have_more_than_four_players_but_only_four_sing_each_song()
    {
        using var store = Memory();
        var players = Enumerable.Range(1, 7).Select(i => store.CreatePlayer($"Jugador {i}")).ToList();
        var session = store.StartSession();
        foreach (var player in players)
        {
            store.AddPlayerToSession(session.Id, player.Id);
        }

        Assert.Equal(7, store.SessionPlayers(session.Id).Count);

        // Canción 1: cantan los primeros cuatro; canción 2: los otros tres en puestos distintos.
        store.SetSelection(session.Id, players.Take(4).Select((p, i) => new SelectionEntry(i + 1, p.Id)).ToList());
        store.SavePerformance(Perf("c1", players.Take(4).Select((p, i) => new ResultInput(p.Id, i + 1, 1000 * (i + 1))).ToArray()));
        store.SetSelection(session.Id, players.Skip(4).Select((p, i) => new SelectionEntry(i + 1, p.Id)).ToList());
        store.SavePerformance(Perf("c2", players.Skip(4).Select((p, i) => new ResultInput(p.Id, i + 1, 500 * (i + 1))).ToArray()));

        var history = store.GetSessionHistory(session.Id);
        Assert.Equal(2, history.Count);
        Assert.Equal(7, history.SelectMany(h => h.Results).Select(r => r.PlayerId).Distinct().Count());
        Assert.Equal(7, store.SessionPlayers(session.Id).Count);
    }

    [Fact]
    public void Choosing_a_player_does_not_add_them_to_the_session()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");
        var session = store.StartSession();

        Assert.Throws<StoreRuleException>(() => store.SetSelection(session.Id, [new SelectionEntry(1, ana.Id)]));
        Assert.Empty(store.SessionPlayers(session.Id));
        Assert.Empty(store.GetSelection(session.Id));
    }

    [Fact]
    public void Players_are_added_and_removed_from_the_open_session_explicitly()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");
        var beto = store.CreatePlayer("Beto");
        var session = store.StartSession(selection: [new SelectionEntry(1, ana.Id)]);

        store.AddPlayerToSession(session.Id, beto.Id);
        store.AddPlayerToSession(session.Id, beto.Id); // repetir no cambia nada
        Assert.Equal(["Ana", "Beto"], store.SessionPlayers(session.Id).Select(p => p.Name));

        store.RemovePlayerFromSession(session.Id, ana.Id);

        Assert.Equal(["Beto"], store.SessionPlayers(session.Id).Select(p => p.Name));
        Assert.Empty(store.GetSelection(session.Id)); // salió también de la selección
    }

    [Fact]
    public void Removing_a_player_from_the_session_keeps_what_they_already_sang()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");
        var session = store.StartSession(selection: [new SelectionEntry(1, ana.Id)]);
        store.SavePerformance(Perf("p1", new ResultInput(ana.Id, 1, 4200)));

        store.RemovePlayerFromSession(session.Id, ana.Id);

        Assert.Single(store.GetSessionHistory(session.Id));
        Assert.Single(store.GetPlayerHistory(ana.Id));
    }

    [Fact]
    public void Adding_archived_players_or_using_a_closed_session_is_refused()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");
        var beto = store.CreatePlayer("Beto");
        store.ArchivePlayer(beto.Id);
        var session = store.StartSession();

        Assert.Throws<StoreRuleException>(() => store.AddPlayerToSession(session.Id, beto.Id));
        store.CloseSession(session.Id);
        Assert.Throws<StoreRuleException>(() => store.AddPlayerToSession(session.Id, ana.Id));
        Assert.Throws<StoreRuleException>(() => store.RemovePlayerFromSession(session.Id, ana.Id));
    }

    [Fact]
    public void Selection_rejects_more_than_four_repeats_bad_slots_and_unusable_players()
    {
        using var store = Memory();
        var players = Enumerable.Range(1, 5).Select(i => store.CreatePlayer($"J{i}")).ToList();
        var session = store.StartSession();
        foreach (var p in players)
        {
            store.AddPlayerToSession(session.Id, p.Id);
        }

        Assert.Throws<StoreRuleException>(() => store.SetSelection(session.Id,
            players.Select((p, i) => new SelectionEntry(i % 4 + 1, p.Id)).ToList()));
        Assert.Throws<StoreRuleException>(() => store.SetSelection(session.Id,
            [new SelectionEntry(1, players[0].Id), new SelectionEntry(1, players[1].Id)]));
        Assert.Throws<StoreRuleException>(() => store.SetSelection(session.Id,
            [new SelectionEntry(1, players[0].Id), new SelectionEntry(2, players[0].Id)]));
        Assert.Throws<StoreRuleException>(() => store.SetSelection(session.Id, [new SelectionEntry(5, players[0].Id)]));
        Assert.Throws<StoreRuleException>(() => store.SetSelection(session.Id, [new SelectionEntry(1, 999)]));

        store.ArchivePlayer(players[0].Id);
        Assert.Throws<StoreRuleException>(() => store.SetSelection(session.Id, [new SelectionEntry(1, players[0].Id)]));
        Assert.Empty(store.GetSelection(session.Id));
    }

    [Fact]
    public void Selection_cannot_change_on_a_closed_session()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");
        var session = store.StartSession();
        store.CloseSession(session.Id);

        Assert.Throws<StoreRuleException>(() => store.SetSelection(session.Id, [new SelectionEntry(1, ana.Id)]));
    }

    [Fact]
    public void Archiving_a_player_removes_them_from_the_open_selection_but_keeps_history()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");
        var beto = store.CreatePlayer("Beto");
        var session = store.StartSession(selection: [new SelectionEntry(1, ana.Id), new SelectionEntry(2, beto.Id)]);
        store.SavePerformance(Perf("p1", new ResultInput(ana.Id, 1, 7000), new ResultInput(beto.Id, 2, 5000)));

        store.ArchivePlayer(ana.Id);

        Assert.Equal([new SelectionEntry(2, beto.Id)], store.GetSelection(session.Id));
        Assert.Single(store.GetPlayerHistory(ana.Id));
        Assert.Equal(2, store.GetSessionHistory(session.Id)[0].Results.Count);
    }

    // ---- Interpretaciones ---------------------------------------------------------------------------------

    [Fact]
    public void Finishing_a_song_without_a_session_creates_one_automatically()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");

        var outcome = store.SavePerformance(Perf("p1", new ResultInput(ana.Id, 1, 8200)));

        Assert.True(outcome.SessionCreated);
        Assert.False(outcome.AlreadySaved);
        Assert.Equal(outcome.SessionId, store.GetOpenSession()!.Id);
        Assert.Equal([new SelectionEntry(1, ana.Id)], store.GetSelection(outcome.SessionId));
        Assert.Single(store.GetSessionHistory(outcome.SessionId));
    }

    [Fact]
    public void An_open_session_is_reused_and_only_accepts_results_from_its_players()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");
        var beto = store.CreatePlayer("Beto");
        var session = store.StartSession(selection: [new SelectionEntry(1, ana.Id)]);

        // Beto no está en la sesión: no se lo incorpora solo, hay que agregarlo.
        Assert.Throws<StoreRuleException>(() =>
            store.SavePerformance(Perf("p1", new ResultInput(ana.Id, 1, 100), new ResultInput(beto.Id, 2, 200))));
        Assert.Empty(store.GetSessionHistory(session.Id));

        store.AddPlayerToSession(session.Id, beto.Id);
        var outcome = store.SavePerformance(Perf("p1", new ResultInput(ana.Id, 1, 100), new ResultInput(beto.Id, 2, 200)));

        Assert.False(outcome.SessionCreated);
        Assert.Equal(session.Id, outcome.SessionId);
        Assert.Equal(2, store.GetSessionHistory(session.Id)[0].Results.Count);
    }

    [Fact]
    public void Saving_the_same_run_twice_does_not_duplicate_anything()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");

        store.SavePerformance(Perf("run-1", new ResultInput(ana.Id, 1, 4000)));
        var second = store.SavePerformance(Perf("run-1", new ResultInput(ana.Id, 1, 9999)));

        Assert.True(second.AlreadySaved);
        var history = store.GetPlayerHistory(ana.Id);
        Assert.Single(history);
        Assert.Equal(4000, history[0].Score);
        Assert.Single(store.ListSessions());
    }

    [Fact]
    public void Results_of_missing_or_archived_players_are_refused()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");
        var beto = store.CreatePlayer("Beto");
        store.ArchivePlayer(beto.Id);

        Assert.Throws<StoreRuleException>(() => store.SavePerformance(Perf("p1", new ResultInput(999, 1, 10))));
        Assert.Throws<StoreRuleException>(() => store.SavePerformance(Perf("p2", new ResultInput(beto.Id, 1, 10))));
        Assert.Empty(store.GetPlayerHistory(ana.Id));
    }

    [Fact]
    public void A_failed_save_leaves_nothing_behind_not_even_the_automatic_session()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");

        // El segundo resultado es de un jugador que no existe: la validación ocurre dentro de la transacción.
        Assert.Throws<StoreRuleException>(() => store.SavePerformance(
            Perf("p1", new ResultInput(ana.Id, 1, 9000), new ResultInput(999, 2, 100))));

        Assert.Null(store.GetOpenSession());
        Assert.Empty(store.ListSessions());
        Assert.Empty(store.GetPlayerHistory(ana.Id));
    }

    [Theory]
    [InlineData(0, 1, 10)]       // sin resultados se prueba aparte
    [InlineData(1, 0, 10)]       // puesto P0
    [InlineData(1, 5, 10)]       // puesto P5
    [InlineData(1, 1, -1)]       // puntaje negativo
    [InlineData(1, 1, 10_001)]   // puntaje sobre el máximo
    public void Out_of_range_slots_and_scores_are_refused(int count, int slot, int score)
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");
        var results = count == 0 ? [] : new[] { new ResultInput(ana.Id, slot, score) };

        Assert.Throws<StoreRuleException>(() => store.SavePerformance(Perf("p1", results)));
        Assert.Empty(store.ListSessions());
    }

    [Fact]
    public void Repeated_slots_or_players_in_one_performance_are_refused_and_five_players_too()
    {
        using var store = Memory();
        var p = Enumerable.Range(1, 5).Select(i => store.CreatePlayer($"J{i}")).ToList();

        Assert.Throws<StoreRuleException>(() => store.SavePerformance(Perf("a",
            new ResultInput(p[0].Id, 1, 1), new ResultInput(p[1].Id, 1, 2))));
        Assert.Throws<StoreRuleException>(() => store.SavePerformance(Perf("b",
            new ResultInput(p[0].Id, 1, 1), new ResultInput(p[0].Id, 2, 2))));
        Assert.Throws<StoreRuleException>(() => store.SavePerformance(Perf("c",
            p.Select((x, i) => new ResultInput(x.Id, i + 1, 100)).ToArray())));
        Assert.Empty(store.ListSessions());
    }

    [Fact]
    public void Positions_share_rank_on_ties_and_are_separate_from_the_microphone_slot()
    {
        using var store = Memory();
        var a = store.CreatePlayer("A");
        var b = store.CreatePlayer("B");
        var c = store.CreatePlayer("C");
        var d = store.CreatePlayer("D");

        var outcome = store.SavePerformance(Perf("p1",
            new ResultInput(a.Id, 1, 5000), new ResultInput(b.Id, 2, 9000),
            new ResultInput(c.Id, 3, 9000), new ResultInput(d.Id, 4, 100)));

        var results = store.GetSessionHistory(outcome.SessionId)[0].Results;
        Assert.Equal([3, 1, 1, 4], results.Select(r => r.Position));
        Assert.Equal([1, 2, 3, 4], results.Select(r => r.Slot));
        Assert.Equal(1, store.GetPlayerHistory(b.Id)[0].Position);
        Assert.Equal(1, store.GetPlayerHistory(c.Id)[0].Position); // empate: comparten el primer puesto
        Assert.Equal(3, store.GetPlayerHistory(a.Id)[0].Position);
        Assert.Equal(4, store.GetPlayerHistory(d.Id)[0].Position);
    }

    [Fact]
    public void Single_player_ranks_first_and_ranking_helper_handles_all_equal()
    {
        Assert.Equal([1], ScoreRanking.Positions([1234]));
        Assert.Equal([1, 1, 1], ScoreRanking.Positions([10, 10, 10]));
        Assert.Equal([1, 2, 3], ScoreRanking.Positions([30, 20, 10]));
    }

    [Fact]
    public void Performance_keeps_a_historical_snapshot_of_song_difficulty_and_scoring_version()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");
        var run = new NewPerformance("p1", SongIdentity.For("Queen", "Bohemian Rhapsody"), "Bohemian Rhapsody", "Queen",
            Difficulty: 2, ScoringVersion: 1, [new ResultInput(ana.Id, 1, 7000)]);

        var outcome = store.SavePerformance(run);
        var saved = store.GetSessionHistory(outcome.SessionId)[0].Performance;

        Assert.Equal("Bohemian Rhapsody", saved.Title);
        Assert.Equal("Queen", saved.Artist);
        Assert.Equal(2, saved.Difficulty);
        Assert.Equal(1, saved.ScoringVersion);
        Assert.Equal(run.SongId, saved.SongId);
    }

    [Fact]
    public void Song_identity_ignores_case_accents_and_spacing_but_not_the_song()
    {
        Assert.Equal(SongIdentity.For("Café Tacvba", "La Ingrata"), SongIdentity.For(" CAFE TACVBA ", "la ingrata"));
        Assert.NotEqual(SongIdentity.For("Queen", "Radio Ga Ga"), SongIdentity.For("Queen", "Under Pressure"));
    }

    // ---- Cierre, reinicio y recuperación ------------------------------------------------------------------

    [Fact]
    public void An_open_session_survives_closing_the_application_with_its_selection_and_results()
    {
        var path = TempDb();
        long sessionId;
        long anaId;
        long betoId;
        using (var store = new KantorStore(path, _clock.Next))
        {
            anaId = store.CreatePlayer("Ana").Id;
            betoId = store.CreatePlayer("Beto").Id;
            var session = store.StartSession("Fiesta",
                [new SelectionEntry(1, anaId), new SelectionEntry(2, betoId)]);
            sessionId = session.Id;
            store.SavePerformance(Perf("p1", new ResultInput(anaId, 1, 6500), new ResultInput(betoId, 2, 7200)));
            // la aplicación se cierra sin cerrar la sesión
        }

        using var reopened = new KantorStore(path, _clock.Next);
        var recovered = reopened.GetOpenSession();

        Assert.NotNull(recovered);
        Assert.Equal(sessionId, recovered!.Id);
        Assert.Equal("Fiesta", recovered.Name);
        Assert.True(recovered.IsOpen);
        Assert.Equal([new SelectionEntry(1, anaId), new SelectionEntry(2, betoId)], reopened.GetSelection(sessionId));
        Assert.Equal(["Ana", "Beto"], reopened.SessionPlayers(sessionId).Select(p => p.Name));
        var history = reopened.GetSessionHistory(sessionId);
        Assert.Single(history);
        Assert.Equal([6500, 7200], history[0].Results.Select(r => r.Score));
    }

    [Fact]
    public void Continuing_after_a_restart_keeps_saving_into_the_same_session()
    {
        var path = TempDb();
        long sessionId;
        long anaId;
        using (var store = new KantorStore(path, _clock.Next))
        {
            anaId = store.CreatePlayer("Ana").Id;
            sessionId = store.SavePerformance(Perf("p1", new ResultInput(anaId, 1, 100))).SessionId;
        }

        using var reopened = new KantorStore(path, _clock.Next);
        var outcome = reopened.SavePerformance(Perf("p2", new ResultInput(anaId, 1, 200)));

        Assert.Equal(sessionId, outcome.SessionId);
        Assert.False(outcome.SessionCreated);
        Assert.Equal(2, reopened.GetSessionHistory(sessionId).Count);
    }

    [Fact]
    public void Starting_a_new_session_with_another_open_closes_it_and_keeps_its_data_after_a_restart()
    {
        var path = TempDb();
        long oldId;
        long anaId;
        using (var store = new KantorStore(path, _clock.Next))
        {
            anaId = store.CreatePlayer("Ana").Id;
            oldId = store.SavePerformance(Perf("p1", new ResultInput(anaId, 1, 3000))).SessionId;
        }

        long newId;
        using (var store = new KantorStore(path, _clock.Next))
        {
            newId = store.StartSession().Id; // "iniciar nueva" con la anterior todavía abierta
        }

        using var reopened = new KantorStore(path, _clock.Next);

        Assert.NotEqual(oldId, newId);
        Assert.Equal(newId, reopened.GetOpenSession()!.Id);
        var old = reopened.ListSessions().Single(s => s.Id == oldId);
        Assert.False(old.IsOpen);
        Assert.Single(reopened.GetSessionHistory(oldId));
        Assert.Empty(reopened.GetSessionHistory(newId));
        Assert.Empty(reopened.GetSelection(newId)); // la sesión nueva no hereda participantes
    }

    [Fact]
    public void A_manually_closed_session_stays_closed_after_a_restart()
    {
        var path = TempDb();
        using (var store = new KantorStore(path, _clock.Next))
        {
            var session = store.StartSession();
            store.CloseSession(session.Id);
        }

        using var reopened = new KantorStore(path, _clock.Next);

        Assert.Null(reopened.GetOpenSession());
    }

    [Fact]
    public void An_interrupted_song_leaves_no_trace_when_nothing_is_saved()
    {
        var path = TempDb();
        long sessionId;
        using (var store = new KantorStore(path, _clock.Next))
        {
            var ana = store.CreatePlayer("Ana");
            sessionId = store.StartSession(selection: [new SelectionEntry(1, ana.Id)]).Id;
            // se canta y se interrumpe: la aplicación nunca llama a SavePerformance
        }

        using var reopened = new KantorStore(path, _clock.Next);

        Assert.Empty(reopened.GetSessionHistory(sessionId));
        Assert.Equal(sessionId, reopened.GetOpenSession()!.Id);
    }

    [Fact]
    public void Archived_players_keep_their_history_across_restarts()
    {
        var path = TempDb();
        long anaId;
        using (var store = new KantorStore(path, _clock.Next))
        {
            anaId = store.CreatePlayer("Ana").Id;
            store.SavePerformance(Perf("p1", new ResultInput(anaId, 1, 5000)));
            store.ArchivePlayer(anaId);
        }

        using var reopened = new KantorStore(path, _clock.Next);

        Assert.True(reopened.GetPlayer(anaId)!.Archived);
        Assert.Single(reopened.GetPlayerHistory(anaId));
        Assert.Empty(reopened.ListPlayers());
    }

    [Fact]
    public void Last_activity_is_updated_when_a_result_is_saved()
    {
        using var store = Memory();
        var ana = store.CreatePlayer("Ana");

        store.SavePerformance(Perf("p1", new ResultInput(ana.Id, 1, 100)));

        Assert.True(store.GetPlayer(ana.Id)!.LastActivityAt > ana.LastActivityAt);
    }
}
