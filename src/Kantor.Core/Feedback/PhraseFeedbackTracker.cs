// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Scoring;

namespace Kantor.Core.Feedback;

/// <summary>
/// Junta las frases que van cerrando los puntuadores de los jugadores y las pasa al
/// <see cref="PhraseFeedbackEngine"/>. No puntúa nada por su cuenta: lee <see cref="VoiceScorer.LineResults"/>.
/// </summary>
/// <remarks>
/// Un mensaje solo se muestra si la frase cerró hace poco (<see cref="MaxAge"/>) y es la última de su jugador en
/// ese sondeo: tras una pausa larga, un salto o al activar la opción, lo viejo no aparece. Si un puntuador vuelve
/// a cero (<see cref="VoiceScorer.Generation"/>), se olvida lo visto de ese jugador.
/// </remarks>
public sealed class PhraseFeedbackTracker
{
    /// <summary>Cuánto después de cerrarse puede mostrarse una frase.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(2);

    private sealed class Entry(int player, VoiceScorer scorer)
    {
        public readonly int Player = player;
        public readonly VoiceScorer Scorer = scorer;
        public int Seen;
        public int Generation = scorer.Generation;
    }

    private readonly PhraseFeedbackEngine _engine;
    private readonly List<Entry> _entries = [];
    private readonly List<LineEvent> _batch = [];

    public PhraseFeedbackTracker(PhraseFeedbackEngine? engine = null) => _engine = engine ?? new PhraseFeedbackEngine();

    /// <summary>
    /// Idioma de los mensajes (el de la interfaz, no el de la canción). Un idioma sin catálogo usa el español.
    /// </summary>
    public void SetLanguage(string? language) => _engine.Catalog = PhraseFeedbackCatalog.For(language);

    /// <summary>
    /// Define los jugadores (número y puntuador). Quien sigue con el mismo puntuador conserva lo visto y su
    /// historial (así refrescar nombres o retratos a mitad de canción no repite mensajes); el resto empieza de cero.
    /// </summary>
    public void SetPlayers(IEnumerable<(int Player, VoiceScorer? Scorer)> players)
    {
        var previous = _entries.ToList();
        _entries.Clear();
        foreach (var (player, scorer) in players)
        {
            if (scorer is null)
            {
                continue;
            }

            var kept = previous.Find(e => e.Player == player && ReferenceEquals(e.Scorer, scorer));
            if (kept is null)
            {
                _engine.Reset(player);
            }

            _entries.Add(kept ?? new Entry(player, scorer));
        }

        foreach (var gone in previous.Where(p => _entries.All(e => e.Player != p.Player)))
        {
            _engine.Reset(gone.Player);
        }
    }

    /// <summary>Mensajes nuevos desde el sondeo anterior, dado el instante actual de la canción.</summary>
    public IReadOnlyList<PhraseFeedback> Poll(TimeSpan position)
    {
        _batch.Clear();
        foreach (var entry in _entries)
        {
            if (entry.Scorer.Generation != entry.Generation)
            {
                entry.Generation = entry.Scorer.Generation;
                entry.Seen = 0;
                _engine.Reset(entry.Player);
            }

            var results = entry.Scorer.LineResults;
            for (var i = entry.Seen; i < results.Count; i++)
            {
                var last = i == results.Count - 1;
                _batch.Add(new LineEvent(entry.Player, results[i],
                    Show: last && position - results[i].EndTime <= MaxAge));
            }

            entry.Seen = results.Count;
        }

        return _batch.Count == 0 ? [] : _engine.Evaluate(_batch, position);
    }
}
