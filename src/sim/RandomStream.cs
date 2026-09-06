namespace Arable;

/// <summary>
/// One named random sequence: a <b>PCG32</b> generator whose whole state is a
/// single <see cref="ulong"/>.
///
/// <b>Why an explicit PRNG in the repo rather than <c>System.Random</c>.</b>
/// The sim has to be reproducible across machines <i>and across time</i> — a
/// save written today has to replay after a .NET upgrade (M10), and #25 hashes
/// the sequence to prove it. Microsoft documents the seeded
/// <c>Random(int)</c> algorithm as an implementation detail, and already
/// changed the unseeded one in .NET 6; the seeded path is compatible <i>so
/// far</i>, which is not a contract anything durable should rest on. Its state
/// is also ~56 ints, which is expensive to save and to walk.
///
/// <b>Why PCG32 specifically</b>, over xorshift/xoshiro:
/// <list type="bullet">
/// <item><b>There is no illegal state.</b> Every 64-bit value is a valid
/// position on the cycle, so restoring a saved state can never land the
/// generator in the all-zeros trap an xorshift family has to be guarded
/// against. That matters most exactly where it is hardest to test — loading a
/// corrupt or hand-edited save.</item>
/// <item><b>The increment is a stream selector.</b> Two PCGs with different odd
/// increments walk genuinely different sequences, not the same cycle from
/// different offsets. That is the per-system-stream idea already built into the
/// generator, so deriving the increment from the stream's <i>name</i> gives
/// each system its own sequence for free — see <see cref="RandomStreams"/>.</item>
/// <item><b>The state is one integer.</b> Saving is one number, restoring
/// mid-sequence is an assignment, and hashing costs one word per stream.</item>
/// </list>
///
/// <b>A reference type on purpose.</b> A mutable value type would make
/// <c>stream.NextInt(...)</c> silently advance a <i>copy</i> the moment one was
/// passed to a method or pulled out of a collection — the classic mutable-struct
/// bug, and here it would surface as a determinism failure rather than as
/// anything obviously wrong. Streams are per <i>system</i>, so there are a
/// handful of them for the whole game and the allocation is irrelevant; what
/// #25 and M10 walk is <see cref="State"/>, which is a plain integer either way.
/// </summary>
public sealed class RandomStream
{
    /// <summary>The 64-bit LCG multiplier from the reference PCG implementation.</summary>
    private const ulong Multiplier = 6364136223846793005UL;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>2^64 / phi — SplitMix64's increment, used here to fold the world seed in.</summary>
    private const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;

    /// <summary>Scale that turns 24 random bits into a float in [0,1) exactly.</summary>
    private const float FloatScale = 1f / 16777216f;

    private ulong _state;

    internal RandomStream(string name, int worldSeed)
    {
        Name = name;

        // PCG needs an odd increment, and the increment is what makes this
        // stream a different sequence rather than an offset into a shared one.
        Increment = (Mix(HashName(name)) << 1) | 1UL;
        Reseed(worldSeed);
    }

    /// <summary>The stream's name — the thing its sequence is derived from.</summary>
    public string Name { get; }

    /// <summary>
    /// The stream selector, derived from <see cref="Name"/> alone and constant
    /// for the life of the stream, so a save need only carry <see cref="State"/>.
    /// </summary>
    public ulong Increment { get; }

    /// <summary>
    /// The entire state of the generator, and therefore the whole of what M10
    /// saves and #25 hashes. <b>Settable, because restoring has to work
    /// mid-sequence</b>: a game saved after 41 draws must resume at draw 42 and
    /// not at the start, or the reloaded run diverges from the one that was
    /// saved. Every value is a legal state (see the class remarks), so a restore
    /// cannot break the generator.
    /// </summary>
    public ulong State
    {
        get => _state;
        set => _state = value;
    }

    /// <summary>
    /// Puts the stream back at the start of its sequence for a world seed —
    /// what "new world" means. Driven by <see cref="RandomStreams.Reseed"/>;
    /// nothing mid-game should be reaching for it.
    /// </summary>
    internal void Reseed(int worldSeed)
    {
        // The reference PCG seeding routine: step the LCG once from zero, add
        // the seed, step it again — so a world seed of 0 is no more special than
        // any other.
        _state = 0UL;
        Step();
        unchecked
        {
            _state += Seed64(worldSeed, Name);
        }
        Step();
    }

    /// <summary>The next 32 random bits. Every other draw is built on this one.</summary>
    public uint NextUInt()
    {
        ulong previous = _state;
        Step();

        // PCG-XSH-RR: xorshift the high bits down, then rotate by a shift count
        // taken from the top of the state. That output permutation is what makes
        // the underlying LCG's weak low bits irrelevant.
        var xorshifted = (uint)(((previous >> 18) ^ previous) >> 27);
        var rotation = (int)(previous >> 59);
        return (xorshifted >> rotation) | (xorshifted << ((-rotation) & 31));
    }

    /// <summary>
    /// A value in <c>0 .. maxExclusive-1</c>, <b>without modulo bias</b> — the
    /// low-effort <c>NextUInt() % n</c> favours the start of the range, which
    /// over a season of price rolls is a thumb on the scale. Answers 0 for a
    /// range of one or fewer, the way asking for a number below 1 ought to.
    /// </summary>
    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 1)
        {
            return 0;
        }

        var bound = (uint)maxExclusive;

        // Reject the short tail at the bottom of the 2^32 range that does not
        // divide evenly, so every value is drawn equally often. Deterministic:
        // the same state always rejects the same draws.
        var threshold = (uint)((0x1_0000_0000UL - bound) % bound);
        uint value;
        do
        {
            value = NextUInt();
        }
        while (value < threshold);

        return (int)(value % bound);
    }

    /// <summary>A value in <c>minInclusive .. maxExclusive-1</c>.</summary>
    public int NextInt(int minInclusive, int maxExclusive) =>
        minInclusive + NextInt(maxExclusive - minInclusive);

    /// <summary>
    /// A float in [0,1). Built from 24 bits, which is every bit a float can hold
    /// without rounding — using all 32 would round some draws up to exactly 1
    /// and quietly break "exclusive".
    /// </summary>
    public float NextFloat() => (NextUInt() >> 8) * FloatScale;

    /// <summary>A float in [min,max).</summary>
    public float NextFloat(float min, float max) => min + NextFloat() * (max - min);

    /// <summary>A coin flip, taken from the strongest bit of the output.</summary>
    public bool NextBool() => (NextUInt() >> 31) != 0U;

    /// <summary>
    /// The 64-bit seed a <paramref name="name"/> gets in a world. Public because
    /// not every consumer of randomness is a stream: <c>FastNoiseLite</c> takes
    /// one int and generates its own field from it, and that int should still be
    /// derived from the one world seed by this rule rather than by adding a
    /// magic offset to the seed — neighbouring noise seeds are not guaranteed to
    /// give unrelated fields.
    /// </summary>
    public static ulong Seed64(int worldSeed, string name)
    {
        unchecked
        {
            // Sign-extending the seed is fine — these are bits, not a number —
            // and multiplying by the golden gamma before mixing keeps adjacent
            // world seeds from producing related streams.
            return Mix(HashName(name) + GoldenGamma * (ulong)(long)worldSeed);
        }
    }

    /// <summary>
    /// FNV-1a over the name's UTF-16 code units, byte by byte.
    ///
    /// <b>Never <c>string.GetHashCode</c>.</b> It is randomised per process on
    /// .NET, so the same name would seed a different stream on every launch — a
    /// determinism bug that reproduces in no single run, and so is invisible to
    /// any test that does not already know to look for it. Hashing the bytes
    /// explicitly is the only version- and platform-independent answer.
    /// </summary>
    public static ulong HashName(string name)
    {
        unchecked
        {
            ulong hash = FnvOffsetBasis;
            foreach (char c in name)
            {
                hash = (hash ^ (byte)c) * FnvPrime;
                hash = (hash ^ (byte)(c >> 8)) * FnvPrime;
            }
            return hash;
        }
    }

    /// <summary>
    /// The SplitMix64 finaliser: an avalanche mix, so one bit in changes about
    /// half the bits out. It is what stops "machines" and "machines2", or world
    /// seeds 7 and 8, from starting anywhere near each other.
    /// </summary>
    public static ulong Mix(ulong value)
    {
        unchecked
        {
            value ^= value >> 30;
            value *= 0xBF58476D1CE4E5B9UL;
            value ^= value >> 27;
            value *= 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return value;
        }
    }

    public override string ToString() => $"{Name}@{_state:X16}";

    private void Step()
    {
        unchecked
        {
            _state = _state * Multiplier + Increment;
        }
    }
}
