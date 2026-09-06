namespace Arable;

/// <summary>
/// Something that <b>writes</b> sim state, advanced once per fixed tick.
///
/// Split from <see cref="ISimView"/> on purpose: the pair is the ownership rule
/// made into types. Today <c>Machine</c> implements both, because one node is
/// still both the state and its own drawing. When entity state moves into flat
/// arrays the array owner keeps this half and the node keeps only the other,
/// and nothing else in the loop has to change.
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
