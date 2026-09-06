using System.Collections.Generic;

namespace Arable;

/// <summary>
/// Slot allocator for sim entities: hands out <see cref="EntityId"/> handles,
/// recycles the slots of destroyed ones, and keeps the generation counters that
/// make a recycled slot distinguishable from the entity that used to hold it.
///
/// <b>It stores no components.</b> Deliberately: the store owns liveness and
/// nothing else, and each system keeps its own parallel arrays sized to
/// <see cref="SlotCount"/> and indexed by <see cref="EntityId.Index"/>. That is
/// the whole "ECS" here — no component registry, no archetypes, no queries.
/// tech.md asks for a data-oriented layout, not a framework; M4/M5 are the
/// milestones that will say what components actually exist, and generalising
/// before then would be guessing.
///
/// <b>Walk it by slot, never by dictionary.</b> <see cref="SlotCount"/> plus
/// <see cref="IsAliveSlot"/> is the only iteration order the store offers, and
/// it is ascending, stable and independent of how entities were created — which
/// is what a determinism hash and a save file both need, and what enumerating a
/// <c>Dictionary</c> would quietly fail to give.
/// </summary>
public sealed class EntityStore
{
    /// <summary>Slots to allocate on first use; it grows by doubling after that.</summary>
    private const int InitialCapacity = 16;

    private int[] _generation = [];
    private bool[] _alive = [];

    // Freed slots, reused last-in-first-out. A list used as a stack, not a
    // queue: both are deterministic, and LIFO keeps reuse in the warm part of
    // the arrays instead of cycling through every slot before repeating.
    private readonly List<int> _free = new();

    /// <summary>Entities currently alive.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Slots ever handed out — the high-water mark, and the exclusive upper
    /// bound for a walk. It never shrinks: component arrays stay valid and a
    /// slot index means the same thing for the life of the run.
    /// </summary>
    public int SlotCount { get; private set; }

    /// <summary>Whether the handle still addresses the entity it was made for.</summary>
    public bool IsAlive(EntityId id) =>
        id.Index >= 0 && id.Index < SlotCount
        && _alive[id.Index] && _generation[id.Index] == id.Generation;

    /// <summary>Whether the slot holds a live entity — the walk's predicate.</summary>
    public bool IsAliveSlot(int index) =>
        index >= 0 && index < SlotCount && _alive[index];

    /// <summary>
    /// The handle for a slot, for turning a walk or a spatial-hash hit back
    /// into an id. Meaningless for a dead slot; check <see cref="IsAliveSlot"/>.
    /// </summary>
    public EntityId IdAt(int index) => new(index, _generation[index]);

    /// <summary>
    /// Allocates a slot. The caller must grow its own component arrays to
    /// cover <see cref="SlotCount"/> and write every component for the new
    /// index — a recycled slot still holds the previous occupant's values.
    /// </summary>
    public EntityId Create()
    {
        int index;
        if (_free.Count > 0)
        {
            index = _free[^1];
            _free.RemoveAt(_free.Count - 1);
        }
        else
        {
            index = SlotCount;
            EnsureCapacity(index + 1);
            SlotCount = index + 1;
            // Live generations start at 1 so default(EntityId) is never alive.
            _generation[index] = 1;
        }

        _alive[index] = true;
        Count++;
        return new EntityId(index, _generation[index]);
    }

    /// <summary>
    /// Frees the entity's slot and invalidates every handle to it. Returns
    /// false if the handle was already stale, which makes a double destroy
    /// harmless rather than a way to free somebody else's entity.
    /// </summary>
    public bool Destroy(EntityId id)
    {
        if (!IsAlive(id))
        {
            return false;
        }

        _alive[id.Index] = false;
        _generation[id.Index]++;
        _free.Add(id.Index);
        Count--;
        return true;
    }

    /// <summary>
    /// Writes liveness into a state hash, so a system only has to hash its own
    /// columns — the slot walk and the generations are the same for all of them.
    ///
    /// The free list is <b>not</b> hashed, only which slots are free: its order
    /// decides which slot the next <see cref="Create"/> hands out, but it is
    /// rebuildable from liveness, and M10 restoring it in a different order
    /// would then fail a save/load comparison over a difference no tick can
    /// observe. The trap that leaves behind: a load that rebuilds the list in
    /// another order hashes equal today and spawns into different slots
    /// tomorrow, so M10 has to restore it in destruction order.
    /// </summary>
    public void HashState(StateHash hash)
    {
        hash.Write(Count);
        hash.Write(SlotCount);
        for (int i = 0; i < SlotCount; i++)
        {
            hash.Write(_alive[i]);
            hash.Write(_generation[i]);
        }
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _generation.Length)
        {
            return;
        }

        int capacity = _generation.Length == 0 ? InitialCapacity : _generation.Length;
        while (capacity < needed)
        {
            capacity *= 2;
        }
        System.Array.Resize(ref _generation, capacity);
        System.Array.Resize(ref _alive, capacity);
    }
}
