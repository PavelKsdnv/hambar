namespace Arable;

/// <summary>
/// Something that <b>writes</b> sim state, advanced once per fixed tick.
///
/// Split from <see cref="ISimView"/> on purpose: the pair is the ownership rule
/// made into types, and the seam entity state moved across. A system is the
/// <b>owner of a set of component arrays</b> — <c>MachineSystem</c> is the
/// first — and it ticks every entity in them; the node that draws one keeps
/// only <see cref="ISimView"/>. Nothing in the loop had to change when that
/// happened, which was the point of splitting the interfaces first.
///
/// Systems tick in registration order and entities within a system in slot
/// order, so the whole tick has one fixed, reproducible sequence.
/// </summary>
public interface ISimSystem
{
    /// <summary>
    /// Advances by exactly one tick. <paramref name="dt"/> is
    /// <see cref="SimClock.TickDelta"/> — always the same number, passed rather
    /// than read so nothing is tempted to use a frame delta instead.
    /// </summary>
    void Tick(float dt);
}
