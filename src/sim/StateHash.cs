using System;
using Godot;

namespace Arable;

/// <summary>
/// A running 64-bit hash of sim state — the fingerprint two runs, or a save and
/// the game it was loaded into, are compared by.
///
/// <b>What it is for.</b> Determinism is a claim that cannot be eyeballed: two
/// runs either produced the same world or they did not, and by the time the
/// difference is visible on screen it happened thousands of ticks ago. A hash
/// taken every tick turns that into a number and a tick index, which is the
/// difference between "the sim diverged" and "the sim diverged on tick 412" —
/// see <see cref="SimStateHash"/> for the walk and <c>SimSmokeTest</c> for the
/// harness. M10 compares the same number across a save/load cycle.
///
/// <b>It hashes bits, not values.</b> A float goes in by its bit pattern, so
/// <c>-0f</c> and <c>0f</c> hash differently and two NaNs with the same payload
/// hash the same. That is the intent: determinism means bit-exact, and a run
/// that reached the same number by a different route <i>is</i> a divergence,
/// even where <c>==</c> would forgive it. Never round or quantise on the way in
/// — that hides exactly the drift this is here to catch.
///
/// <b>A class, not a struct</b>, for the reason <see cref="RandomStream"/> is
/// one: a mutable value type handed to a <see cref="IHashableState.HashState"/>
/// implementation without <c>ref</c> would accumulate into a copy and the
/// caller's hash would silently stay empty. A hash that is silently wrong is
/// worse than no hash at all, and no signature can be got wrong here.
/// </summary>
public sealed class StateHash
{
    /// <summary>
    /// The empty hash. Not zero: a zero start would leave a leading run of
    /// zero words indistinguishable from no words at all.
    /// </summary>
    private const ulong Seed = 0x9E3779B97F4A7C15UL;

    private ulong _hash = Seed;

    /// <summary>The hash of everything written so far.</summary>
    public ulong Value => _hash;

    /// <summary>
    /// Empties it for reuse. The point is the inner loop of an unordered fold,
    /// where one scratch hasher does every member instead of one per member.
    /// </summary>
    public void Reset() => _hash = Seed;

    /// <summary>
    /// The one primitive: every other overload lands here. Chaining through
    /// SplitMix64 rather than FNV's byte loop costs one multiply-shift per word
    /// instead of eight, which is what makes hashing a 9409-cell terrain layer
    /// on every tick affordable.
    /// </summary>
    public void Write(ulong value) => _hash = RandomStream.Mix(_hash ^ value);

    public void Write(long value) => Write((ulong)value);

    public void Write(int value) => Write((ulong)(long)value);

    public void Write(uint value) => Write((ulong)value);

    /// <summary>1 and 2, not 1 and 0, so a bool cannot alias an int written beside it.</summary>
    public void Write(bool value) => Write(value ? 1UL : 2UL);

    public void Write(float value) => Write(BitConverter.SingleToUInt32Bits(value));

    public void Write(double value) => Write(BitConverter.DoubleToUInt64Bits(value));

    /// <summary>
    /// By <see cref="RandomStream.HashName"/> — explicitly <b>not</b>
    /// <c>string.GetHashCode</c>, which .NET randomises per process and would
    /// give the same state a different hash on every launch.
    /// </summary>
    public void Write(string value) => Write(RandomStream.HashName(value));

    public void Write(Vector2I value)
    {
        Write(value.X);
        Write(value.Y);
    }

    public void Write(Vector3 value)
    {
        Write(value.X);
        Write(value.Y);
        Write(value.Z);
    }

    /// <summary>
    /// Writes a collection whose <i>iteration order is not part of the state</i>
    /// — a <c>Dictionary</c> of placed tiles, a registry keyed by id — from the
    /// commutative <see cref="Fold"/> of its members plus how many there were.
    ///
    /// <b>This is the false failure the whole file exists to avoid.</b>
    /// Enumerating a hash map gives an order that depends on insertion history
    /// and on internal capacity, so two runs holding identical state can walk
    /// it differently; hashing that walk in order reports a divergence that is
    /// not one, and the afternoon is spent hunting a bug in the sim. The count
    /// goes in with the fold because addition alone cannot tell one member from
    /// two that happen to sum to it.
    /// </summary>
    public void WriteUnordered(ulong fold, int count)
    {
        Write(fold);
        Write(count);
    }

    /// <summary>
    /// Adds one member into an unordered fold. Addition of well-mixed words,
    /// not xor: xor cancels a member against its duplicate, and a pair of
    /// identical rows is a state worth telling apart from none.
    /// </summary>
    public static ulong Fold(ulong fold, ulong member) =>
        unchecked(fold + RandomStream.Mix(member));
}
