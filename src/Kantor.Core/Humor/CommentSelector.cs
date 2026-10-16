// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kantor.Core.Humor;

/// <summary>Fuente de azar inyectable (determinista en tests).</summary>
public interface IRandomSource
{
    /// <summary>Entero en [0, maxExclusive).</summary>
    int Next(int maxExclusive);

    /// <summary>Real en [0, 1).</summary>
    double NextDouble();
}

public sealed class SystemRandomSource : IRandomSource
{
    private readonly Random _random;
    public SystemRandomSource(Random? random = null) => _random = random ?? new Random();
    public int Next(int maxExclusive) => _random.Next(maxExclusive);
    public double NextDouble() => _random.NextDouble();
}

/// <summary>
/// Bolsa mezclada: entrega cada elemento una vez antes de repetir. Al agotarse se
/// remezcla evitando que el primero coincida con el último entregado.
/// </summary>
public sealed class ShuffleBag
{
    private readonly string[] _items;
    private readonly IRandomSource _random;
    private readonly List<string> _pending = new();
    private string? _last;

    public ShuffleBag(IReadOnlyList<string> items, IRandomSource random)
    {
        if (items.Count == 0) throw new ArgumentException("La bolsa no puede estar vacía.", nameof(items));
        _items = items.ToArray();
        _random = random;
    }

    public int Count => _items.Length;

    public string Next()
    {
        if (_pending.Count == 0) Refill();
        var id = _pending[^1];
        _pending.RemoveAt(_pending.Count - 1);
        _last = id;
        return id;
    }

    private void Refill()
    {
        _pending.AddRange(_items);
        for (var i = _pending.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (_pending[i], _pending[j]) = (_pending[j], _pending[i]);
        }
        // Se entrega desde el final: que el próximo no repita el último.
        if (_items.Length > 1 && _pending[^1] == _last)
            (_pending[^1], _pending[0]) = (_pending[0], _pending[^1]);
    }
}

/// <summary>Comentario elegido: el ID estable manda; el texto depende del idioma.</summary>
public readonly record struct HumorPick(string Id, HumorCategory Category, bool Nonsense);

/// <summary>
/// Elige el comentario de un resultado. 10000 tiene prioridad absoluta; el
/// "Random Nonsense" solo aplica a 0–9999 y nunca en resultados consecutivos.
/// </summary>
public sealed class CommentSelector
{
    public const double DefaultNonsenseProbability = 0.03;

    private readonly Dictionary<HumorCategory, ShuffleBag> _bags = new();
    private readonly IRandomSource _random;
    private bool _lastWasNonsense;

    public double NonsenseProbability { get; }

    public CommentSelector(HumorBank ids, IRandomSource random, double nonsenseProbability = DefaultNonsenseProbability)
    {
        _random = random;
        NonsenseProbability = Math.Clamp(nonsenseProbability, 0.0, 1.0);
        foreach (HumorCategory c in Enum.GetValues<HumorCategory>())
            _bags[c] = new ShuffleBag(ids.Ids(c), random);
    }

    public HumorPick Pick(int score)
    {
        score = Math.Clamp(score, 0, HumorRanges.PerfectScore);
        if (score >= HumorRanges.PerfectScore)
        {
            _lastWasNonsense = false;
            return new HumorPick(_bags[HumorCategory.Divine].Next(), HumorCategory.Divine, false);
        }

        if (!_lastWasNonsense && NonsenseProbability > 0 && _random.NextDouble() < NonsenseProbability)
        {
            _lastWasNonsense = true;
            return new HumorPick(_bags[HumorCategory.Random].Next(), HumorCategory.Random, true);
        }

        _lastWasNonsense = false;
        var category = HumorRanges.CategoryFor(score);
        return new HumorPick(_bags[category].Next(), category, false);
    }
}
