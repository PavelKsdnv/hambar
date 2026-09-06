using System;

namespace Arable;

/// <summary>
/// A container of item stacks with a <b>hard capacity</b>: what a field's
/// harvest goes into, what M5's trucks come to empty, and what M6's machines
/// draw from and back up against.
///
/// <b>The capacity is the point, not a safety rail.</b> Nothing consumes from a
/// field's buffer yet, so a full one has no way to empty in M4 — and it exists
/// anyway, because "the output is full, so production stops" is the backpressure
/// M6's chains are made of, and a buffer that grew without limit would let every
/// producer in the game run forever whether or not anything collected from it.
/// The number that overflows is therefore a real refusal today
/// (<see cref="CropOpResult.OutputFull"/>) rather than a warning added later.
///
/// <b>Capacity counts units, not stacks.</b> One number for "how much is in
/// here" is what a UI shows and what a hauling job divides into loads;
/// per-good limits would need a rule for how a mixed buffer splits its room and
/// nothing in the design has asked for one. A buffer holding several goods
/// therefore shares its room between them, first come first served.
///
/// <b>Stacks are held ascending by <see cref="ItemType.Id"/>, one per good.</b>
/// Merging on insert keeps "how much grain is in here" a single number rather
/// than a walk, and the sorted order makes the hash walk canonical without a
/// fold: the same contents are the same array however they were added, which is
/// what <c>### State hashing</c> asks of any collection whose order is not
/// itself state.
/// </summary>
public sealed class ItemBuffer
{
    /// <summary>
    /// Goods a buffer starts with room for. Two, because a field's holds one
    /// and a mill's holds a handful; it doubles from there.
    /// </summary>
    private const int InitialStacks = 2;

    private ItemStack[] _stacks = [];
    private int _count;

    public ItemBuffer(int capacity)
    {
        Capacity = Math.Max(0, capacity);
    }

    /// <summary>
    /// Units this buffer may hold across every good in it. Never negative;
    /// zero is a legal buffer that refuses everything, which is what a
    /// producer with nowhere to put its output looks like.
    /// </summary>
    public int Capacity { get; private set; }

    /// <summary>Units in here, over all goods.</summary>
    public int Total { get; private set; }

    /// <summary>
    /// Room left. Floored at zero, because <see cref="SetCapacity"/> can leave
    /// a buffer holding more than it may now take.
    /// </summary>
    public int Free => Math.Max(0, Capacity - Total);

    /// <summary>
    /// Whether anything more can go in. The state M6 polls: a producer whose
    /// output buffer is full has to stop rather than delete what it made.
    /// </summary>
    public bool IsFull => Total >= Capacity;

    public bool IsEmpty => Total == 0;

    /// <summary>Distinct goods in here — the bound of a walk over the contents.</summary>
    public int StackCount => _count;

    /// <summary>
    /// The stack at that position, ascending by <see cref="ItemType.Id"/>. The
    /// order is a property of the contents and not of the order they arrived,
    /// so a walk over it is safe to hash and to draw.
    /// </summary>
    public ItemStack StackAt(int index) => _stacks[index];

    /// <summary>How many units of one good are in here. Zero for a good that is not.</summary>
    public int CountOf(ItemType type)
    {
        int at = IndexOf(type);
        return at < 0 ? 0 : _stacks[at].Quantity;
    }

    /// <summary>Whether that many units would fit right now.</summary>
    public bool HasRoomFor(int quantity) => quantity <= Free;

    /// <summary>
    /// Puts in as much as fits and answers how much went in — the partial door,
    /// for a caller pouring from a load it can keep the rest of.
    /// </summary>
    public int Add(ItemType type, int quantity)
    {
        if (type.IsNone || quantity <= 0)
        {
            return 0;
        }

        int accepted = Math.Min(quantity, Free);
        if (accepted <= 0)
        {
            return 0;
        }

        int at = IndexOf(type);
        if (at >= 0)
        {
            _stacks[at] = _stacks[at] with { Quantity = _stacks[at].Quantity + accepted };
        }
        else
        {
            Insert(~at, new ItemStack(type, accepted));
        }
        Total += accepted;
        return accepted;
    }

    /// <summary>
    /// Puts the whole lot in, or <b>nothing at all</b>. The door a harvest
    /// takes: a partly deposited yield would need a "half cut" field state that
    /// the crop state machine does not have, so the refusal leaves the crop
    /// standing instead of inventing one.
    /// </summary>
    public bool TryAdd(ItemType type, int quantity)
    {
        if (type.IsNone || quantity <= 0 || !HasRoomFor(quantity))
        {
            return false;
        }
        Add(type, quantity);
        return true;
    }

    /// <summary>
    /// Takes up to that many units out and answers how many came — the seam
    /// M5's collection runs through, and the only way a full buffer ever
    /// unblocks. Emptied stacks are dropped, so a good that is gone is not a
    /// zero row a walk still has to skip.
    /// </summary>
    public int Remove(ItemType type, int quantity)
    {
        int at = IndexOf(type);
        if (at < 0 || quantity <= 0)
        {
            return 0;
        }

        int taken = Math.Min(quantity, _stacks[at].Quantity);
        int left = _stacks[at].Quantity - taken;
        if (left > 0)
        {
            _stacks[at] = _stacks[at] with { Quantity = left };
        }
        else
        {
            RemoveAt(at);
        }
        Total -= taken;
        return taken;
    }

    /// <summary>Throws away the contents, keeping the capacity.</summary>
    public void Clear()
    {
        Array.Clear(_stacks, 0, _count);
        _count = 0;
        Total = 0;
    }

    /// <summary>
    /// Resizes the container. <b>Contents are never dropped to fit</b> — a
    /// field that shrank under a full buffer holds more than it may take until
    /// something collects, which reads as "no more room" rather than as grain
    /// vanishing because the player bulldozed a corner.
    /// </summary>
    public void SetCapacity(int capacity) => Capacity = Math.Max(0, capacity);

    /// <summary>
    /// Contents and capacity, in the canonical stack order. The capacity goes
    /// in because it decides what every future deposit does, exactly as the
    /// crop thresholds do.
    /// </summary>
    public void HashState(StateHash hash)
    {
        hash.Write(Capacity);
        hash.Write(Total);
        hash.Write(_count);
        for (int i = 0; i < _count; i++)
        {
            hash.Write(_stacks[i].Type.Id);
            hash.Write(_stacks[i].Quantity);
        }
    }

    public override string ToString() => $"{Total}/{Capacity}";

    /// <summary>
    /// Where the good is, or the bitwise complement of where it would be
    /// inserted — the <c>Array.BinarySearch</c> convention, so one search
    /// answers both questions. Linear, because a buffer holds a handful of
    /// goods and a binary search over four entries is slower and easier to get
    /// wrong.
    /// </summary>
    private int IndexOf(ItemType type)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_stacks[i].Type.Id == type.Id)
            {
                return i;
            }
            if (_stacks[i].Type.Id > type.Id)
            {
                return ~i;
            }
        }
        return ~_count;
    }

    private void Insert(int at, ItemStack stack)
    {
        if (_count == _stacks.Length)
        {
            Array.Resize(ref _stacks, Math.Max(InitialStacks, _stacks.Length * 2));
        }
        for (int i = _count; i > at; i--)
        {
            _stacks[i] = _stacks[i - 1];
        }
        _stacks[at] = stack;
        _count++;
    }

    private void RemoveAt(int at)
    {
        for (int i = at; i < _count - 1; i++)
        {
            _stacks[i] = _stacks[i + 1];
        }
        _count--;
        _stacks[_count] = default;
    }
}
