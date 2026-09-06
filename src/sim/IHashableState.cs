namespace Arable;

/// <summary>
/// Something that owns sim state and can write it into a <see cref="StateHash"/>.
///
/// <b>State hashes itself.</b> The walker (<see cref="SimStateHash"/>) never
/// reaches into anyone's arrays: it knows the clock, the calendar and the RNG
/// streams because <c>Simulation</c> owns those, and for everything else it
/// asks. That is what keeps the hash correct as M4 and M5 add columns — the
/// milestone that adds a field to a system adds one line to that system's
/// <see cref="HashState"/>, in the file it is already editing, instead of
/// discovering months later that the hash never covered it.
///
/// <b>What to leave out: anything derived.</b> A spatial index, a planned
/// route, a cell-to-owner lookup — anything rebuildable from the state beside
/// it is not hashed, for the same reason M10 will not save it. Hashing derived
/// state makes a save/load comparison fail over a rebuild that was perfectly
/// correct.
///
/// An <see cref="ISimSystem"/> that implements this is walked automatically on
/// registration; state that is not a system (the world grid, the balance)
/// registers itself with <c>Simulation.RegisterState</c>.
/// </summary>
public interface IHashableState
{
    /// <summary>
    /// A stable name for this state. It is hashed with the contents, so two
    /// sources cannot swap their values unnoticed, and it is what makes the
    /// walk over sources independent of registration order.
    /// </summary>
    string StateName { get; }

    /// <summary>
    /// Writes every piece of state this owns, in an order that depends on
    /// nothing but the state itself — a slot walk, an index walk, or
    /// <see cref="StateHash.WriteUnordered"/> for a collection whose order is
    /// not state.
    /// </summary>
    void HashState(StateHash hash);
}
