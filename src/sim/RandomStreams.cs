using System;
using System.Collections.Generic;

namespace Arable;

/// <summary>
/// The world's randomness: <b>one seed</b>, and a named
/// <see cref="RandomStream"/> per system drawn from it.
///
/// <b>The problem this exists to remove.</b> A single shared pool is the classic
/// determinism trap: every consumer draws from the same sequence, so adding one
/// roll anywhere shifts what every later consumer gets. The failure is silent —
/// the game still runs, it just stops being the game that was saved, and the
/// systems it breaks first (M7's price series, M9's pest events) are exactly the
/// ones nobody can eyeball for correctness.
///
/// <b>The fix is derivation, not partitioning.</b> A stream's sequence comes
/// from <c>(worldSeed, name)</c> and from nothing else — not from a position in
/// a list, not from a registration order, not from how many other streams exist.
/// So a system added in M9 cannot disturb a stream registered in M3, and
/// registering the same streams in a different order gives the identical game.
/// <see cref="RandomStream.Seed64"/> mixes the pair, and the name also picks the
/// PCG increment, so two streams walk different sequences rather than the same
/// one from different offsets.
///
/// <b>Names live with the system that owns them</b> (<c>MachineSystem.StreamName</c>,
/// <c>WorldGrid.SpawnStreamName</c>) rather than in one central list here.
/// A central list is a file every new system has to edit and a merge conflict
/// every milestone; the name belongs beside the draws it feeds. The cost is that
/// a typo silently opens a <i>new</i> stream instead of failing — which is why
/// they are constants and not literals at the call site.
///
/// <b>Order for #25 and M10.</b> <see cref="Ordered"/> is sorted by name, not by
/// creation, so a hash or a save walks streams in an order that does not depend
/// on which systems happened to draw first. Each stream contributes exactly one
/// <c>ulong</c> (<see cref="RandomStream.State"/>); the increment is re-derived
/// from the name on load and does not need saving.
/// </summary>
public sealed class RandomStreams
{
    /// <summary>
    /// The default world seed. Lives here rather than on a node because it is
    /// the seed for the <i>world</i>, not for the terrain generator that happens
    /// to be its loudest consumer.
    /// </summary>
    public const int DefaultWorldSeed = 20260904;

    private static readonly Comparer<RandomStream> ByName =
        Comparer<RandomStream>.Create((a, b) => string.CompareOrdinal(a.Name, b.Name));

    private readonly List<RandomStream> _ordered = new();
    private readonly Dictionary<string, RandomStream> _byName = new(StringComparer.Ordinal);

    public RandomStreams(int worldSeed = DefaultWorldSeed) => WorldSeed = worldSeed;

    /// <summary>The one seed every stream in this world is derived from.</summary>
    public int WorldSeed { get; private set; }

    /// <summary>Streams opened so far. A stream nobody has asked for does not exist.</summary>
    public int Count => _ordered.Count;

    /// <summary>
    /// Every open stream, ordinal by name — the fixed walk #25 hashes and M10
    /// saves. Ordering by name rather than by creation is what keeps that walk
    /// independent of which system drew first.
    /// </summary>
    public IReadOnlyList<RandomStream> Ordered => _ordered;

    /// <summary>
    /// The stream called <paramref name="name"/>, opening it at the start of its
    /// sequence the first time it is asked for. <b>Same object every time</b>, so
    /// a system takes its stream once and keeps it: the state advances where the
    /// holder sees it, and a <see cref="Reseed"/> reaches held references too.
    /// </summary>
    public RandomStream For(string name)
    {
        if (_byName.TryGetValue(name, out RandomStream? existing))
        {
            return existing;
        }

        var stream = new RandomStream(name, WorldSeed);
        _byName[name] = stream;
        int at = _ordered.BinarySearch(stream, ByName);
        _ordered.Insert(at < 0 ? ~at : at, stream);
        return stream;
    }

    /// <summary>Whether a stream has been opened — for tests asserting the wiring.</summary>
    public bool Has(string name) => _byName.ContainsKey(name);

    /// <summary>
    /// Starts a new world: every open stream jumps back to the beginning of its
    /// sequence for <paramref name="worldSeed"/>, in place, so whoever is
    /// holding one keeps drawing from the right world. This is <b>not</b> how a
    /// save is restored — that puts each stream back mid-sequence via
    /// <see cref="RandomStream.State"/>, which is the whole reason that setter
    /// exists.
    /// </summary>
    public void Reseed(int worldSeed)
    {
        WorldSeed = worldSeed;
        for (int i = 0; i < _ordered.Count; i++)
        {
            _ordered[i].Reseed(worldSeed);
        }
    }

    /// <summary>
    /// A derived <c>int</c> seed for a consumer that generates its own field
    /// rather than drawing a sequence — <c>FastNoiseLite</c> is the one that
    /// exists. It comes off the same derivation as a stream, so "one world seed"
    /// stays true of the terrain as well, and there is no offset constant to
    /// wonder about.
    /// </summary>
    public int DeriveSeed(string name) => (int)(uint)RandomStream.Seed64(WorldSeed, name);
}
