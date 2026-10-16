// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Scoring;

namespace Kantor.Core.Feedback;

public enum FeedbackKind
{
    /// <summary>Valoración individual de la frase.</summary>
    Rating,

    /// <summary>Varias frases buenas seguidas.</summary>
    Streak,

    /// <summary>Frase buena después de varias flojas.</summary>
    Recovery,

    /// <summary>Dos jugadores con frase perfecta a la vez.</summary>
    Duel,
}

/// <summary>Un mensaje para mostrar en la zona de un jugador. <paramref name="ShownAt"/> es posición de la canción.</summary>
public sealed record PhraseFeedback(int Player, string Text, PhraseRating Rating, FeedbackKind Kind, TimeSpan ShownAt);

/// <summary>Una frase cerrada de un jugador. <paramref name="Show"/> en falso: solo cuenta para rachas, no se muestra.</summary>
public readonly record struct LineEvent(int Player, LineResult Result, bool Show = true);

/// <summary>
/// Decide qué mensaje corresponde a cada frase cerrada. No mira el puntaje acumulado ni compara jugadores entre
/// sí salvo por un hecho: que dos hayan hecho una frase perfecta a la vez. Cada jugador tiene su propio historial.
/// </summary>
/// <remarks>
/// Prioridad: duelo (dos perfectas simultáneas) &gt; recuperación &gt; racha &gt; valoración individual. La racha
/// no reemplaza a una frase perfecta. Racha: cada <see cref="StreakEvery"/> frases buenas seguidas. Recuperación:
/// frase buena tras <see cref="RecoveryAfterWeak"/> flojas seguidas.
/// </remarks>
public sealed class PhraseFeedbackEngine
{
    public const int StreakEvery = 4;
    public const int RecoveryAfterWeak = 2;
    public const int HistorySize = 4;

    private sealed class PlayerState
    {
        public int Streak;
        public int WeakRun;
        public readonly Queue<string> Recent = new();
    }

    private readonly Dictionary<int, PlayerState> _players = [];
    private readonly Random _random;

    /// <summary>
    /// Catálogo con el que se arman los mensajes; se puede cambiar en cualquier momento (idioma de la interfaz).
    /// Un mensaje nunca mezcla idiomas: se elige entero de un solo catálogo.
    /// </summary>
    public PhraseFeedbackCatalog Catalog { get; set; }

    public PhraseFeedbackEngine(Random? random = null, PhraseFeedbackCatalog? catalog = null)
    {
        _random = random ?? new Random();
        Catalog = catalog ?? PhraseFeedbackCatalog.Spanish;
    }

    /// <summary>Olvida rachas e historial de un jugador (canción nueva o reinicio).</summary>
    public void Reset(int player) => _players.Remove(player);

    public void ResetAll() => _players.Clear();

    /// <summary>
    /// Procesa las frases cerradas en un mismo instante. Devuelve un mensaje por cada evento con
    /// <see cref="LineEvent.Show"/>; los demás solo actualizan rachas.
    /// </summary>
    public IReadOnlyList<PhraseFeedback> Evaluate(IReadOnlyList<LineEvent> events, TimeSpan shownAt)
    {
        var outcomes = new List<(LineEvent Event, PlayerState State, PhraseRating Rating, FeedbackKind Kind)>();
        foreach (var e in events)
        {
            if (!_players.TryGetValue(e.Player, out var state))
            {
                _players[e.Player] = state = new PlayerState();
            }

            var rating = PhraseRatings.Classify(e.Result.Fraction);
            var good = e.Result.Fraction >= PhraseRatings.GoodRunFraction;
            var kind = FeedbackKind.Rating;
            if (good && state.WeakRun >= RecoveryAfterWeak)
            {
                kind = FeedbackKind.Recovery;
            }

            state.WeakRun = e.Result.Fraction < PhraseRatings.WeakFraction ? state.WeakRun + 1 : 0;
            state.Streak = good ? state.Streak + 1 : 0;
            if (kind == FeedbackKind.Rating && state.Streak > 0 && state.Streak % StreakEvery == 0
                && rating < PhraseRating.Perfect)
            {
                kind = FeedbackKind.Streak;
            }

            outcomes.Add((e, state, rating, kind));
        }

        // Duelo: dos o más jugadores distintos con frase perfecta en el mismo cierre (y a la vista).
        var perfect = outcomes.Where(o => o.Event.Show && o.Rating == PhraseRating.Perfect)
            .Select(o => o.Event.Player).Distinct().Count();
        var duel = perfect >= 2;
        var duelText = duel ? Catalog.Duel[_random.Next(Catalog.Duel.Count)] : null;

        var result = new List<PhraseFeedback>();
        foreach (var (e, state, rating, kind) in outcomes)
        {
            if (!e.Show)
            {
                continue;
            }

            string text;
            var finalKind = kind;
            if (duel && rating == PhraseRating.Perfect)
            {
                text = duelText!;
                finalKind = FeedbackKind.Duel;
                Remember(state, text);
            }
            else
            {
                var pool = kind switch
                {
                    FeedbackKind.Recovery => Catalog.Recovery,
                    FeedbackKind.Streak => Catalog.Streak,
                    _ => Catalog.ByRating[rating],
                };
                text = Pick(pool, state);
            }

            result.Add(new PhraseFeedback(e.Player, text, rating, finalKind, shownAt));
        }

        return result;
    }

    /// <summary>Al azar dentro del grupo, sin repetir los últimos mensajes de ese jugador.</summary>
    private string Pick(IReadOnlyList<string> pool, PlayerState state)
    {
        var candidates = pool.Where(t => !state.Recent.Contains(t)).ToList();
        if (candidates.Count == 0)
        {
            var last = state.Recent.LastOrDefault();
            candidates = pool.Where(t => t != last).ToList();
        }

        if (candidates.Count == 0)
        {
            candidates = [.. pool];
        }

        var text = candidates[_random.Next(candidates.Count)];
        Remember(state, text);
        return text;
    }

    private static void Remember(PlayerState state, string text)
    {
        state.Recent.Enqueue(text);
        while (state.Recent.Count > HistorySize)
        {
            state.Recent.Dequeue();
        }
    }
}
