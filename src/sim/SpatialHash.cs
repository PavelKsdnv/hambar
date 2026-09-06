using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// "What is near this cell?", answered without scanning every entity: a bucket
/// of <see cref="EntityId"/> per grid cell.
///
/// Keyed by <b>grid cell</b> rather than by a bucket size of its own, because
/// the world is already cell-addressed and <c>WorldGrid</c> owns the cell math
/// — a second granularity would mean a second rounding rule to keep in step
/// with it. Cells are 2 m, which is the scale neighbourhood questions are asked
/// at (who is on this road cell, what is beside this building).
///
/// <b>Derived state, not sim truth.</b> The component arrays are authoritative;
/// this is an index over them that its owner keeps in step through
/// <see cref="Move"/>. So it is rebuildable, it must not be serialized, and it
/// must not feed a determinism hash — hash the arrays.
///
/// <b>Never enumerate the dictionary.</b> Bucket iteration order is a hash-map
/// detail and would leak into results; every query here walks an explicit,
/// ascending range of cells instead, so the same world state always answers in
/// the same order.
/// </summary>
public sealed class SpatialHash
{
    private static readonly EntityId[] Nothing = [];

    private readonly Dictionary<Vector2I, List<EntityId>> _buckets = new();

    /// <summary>Occupied cells. Empty buckets are dropped, so this is the live count.</summary>
    public int OccupiedCells => _buckets.Count;

    public void Insert(Vector2I cell, EntityId id)
    {
        if (!_buckets.TryGetValue(cell, out List<EntityId>? bucket))
        {
            bucket = new List<EntityId>(2);
            _buckets[cell] = bucket;
        }
        if (!bucket.Contains(id))
        {
            bucket.Add(id);
        }
    }

    public void Remove(Vector2I cell, EntityId id)
    {
        if (!_buckets.TryGetValue(cell, out List<EntityId>? bucket))
        {
            return;
        }
        bucket.Remove(id);
        if (bucket.Count == 0)
        {
            // Dropped, not kept: movers roam the whole map, and keeping every
            // cell they ever touched would grow the index to map size.
            _buckets.Remove(cell);
        }
    }

    /// <summary>
    /// Re-files an entity that moved. A no-op when the cell did not change,
    /// which is the common case every tick — a machine crosses a cell boundary
    /// far less often than it moves.
    /// </summary>
    public void Move(Vector2I from, Vector2I to, EntityId id)
    {
        if (from == to)
        {
            return;
        }
        Remove(from, id);
        Insert(to, id);
    }

    public void Clear() => _buckets.Clear();

    /// <summary>
    /// The entities filed under one cell, in insertion order. The returned list
    /// is the live bucket — read it, do not keep it across a mutation.
    /// </summary>
    public IReadOnlyList<EntityId> At(Vector2I cell) =>
        _buckets.TryGetValue(cell, out List<EntityId>? bucket) ? bucket : Nothing;

    /// <summary>
    /// Appends everything within <paramref name="radius"/> cells of
    /// <paramref name="center"/> (a square neighbourhood, Chebyshev distance)
    /// to <paramref name="into"/>. The caller supplies the list so a query in a
    /// tick loop allocates nothing; cells are visited in ascending z then x, so
    /// the order is a property of the world and not of the index.
    /// </summary>
    public void Query(Vector2I center, int radius, List<EntityId> into)
    {
        for (int z = center.Y - radius; z <= center.Y + radius; z++)
        {
            for (int x = center.X - radius; x <= center.X + radius; x++)
            {
                if (_buckets.TryGetValue(new Vector2I(x, z), out List<EntityId>? bucket))
                {
                    into.AddRange(bucket);
                }
            }
        }
    }
}
