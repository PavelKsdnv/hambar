namespace Arable;

/// <summary>
/// A handle to one sim entity: which slot in the parallel arrays it lives in,
/// and <b>which occupant of that slot</b> it is.
///
/// The generation is the whole point. Slots are recycled, so an index alone
/// cannot tell "the machine I spawned" from "whatever moved into its slot after
/// it was destroyed" — a stale index would silently address a different entity
/// and the bug would look like teleportation. <see cref="EntityStore.Destroy"/>
/// bumps the slot's generation, so every handle to the old occupant stops
/// matching at once, and <see cref="EntityStore.IsAlive"/> is the one question
/// worth asking before dereferencing a handle held across ticks.
///
/// Live generations start at 1, which makes <c>default(EntityId)</c> —
/// index 0, generation 0 — permanently dead even though slot 0 is a real slot.
/// That is why <see cref="None"/> can just be the default value.
/// </summary>
public readonly record struct EntityId(int Index, int Generation)
{
    /// <summary>The handle that is never alive. Safe as a field's initial value.</summary>
    public static EntityId None => default;

    public override string ToString() => $"e{Index}.{Generation}";
}
