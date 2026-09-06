using System;

namespace Arable;

/// <summary>
/// The fixed-timestep accumulator: real seconds in, whole sim ticks out, with
/// the leftover fraction of a tick left behind as <see cref="Alpha"/> for the
/// view to interpolate with.
///
/// Deliberately a plain class and not a <c>Node</c> — the tick schedule is
/// arithmetic, and keeping it out of the scene tree is what lets a test feed it
/// a frame pattern no real frame rate would produce.
/// </summary>
public sealed class SimClock
{
    /// <summary>
    /// Sim ticks per simulated second. 20 Hz is deliberately *not* Godot's
    /// 60 Hz physics rate, so a coupling bug shows up as a threefold error
    /// rather than hiding behind a matching cadence.
    /// </summary>
    public const int DefaultTickRate = 20;

    /// <summary>
    /// Ticks a single frame may run before the clock gives up on catching
    /// up — the spiral-of-death guard. Five ticks is 0.25 s of sim per frame.
    /// </summary>
    public const int DefaultMaxTicksPerFrame = 5;

    private double _accumulator;

    public SimClock(int tickRate = DefaultTickRate,
        int maxTicksPerFrame = DefaultMaxTicksPerFrame)
    {
        TickRate = Math.Max(1, tickRate);
        MaxTicksPerFrame = Math.Max(1, maxTicksPerFrame);
        TickDelta = 1.0 / TickRate;
    }

    /// <summary>Ticks per simulated second.</summary>
    public int TickRate { get; }

    /// <summary>Seconds of sim time one tick advances. Never varies.</summary>
    public double TickDelta { get; }

    /// <summary>Ceiling on ticks run in one call to <see cref="Advance"/>.</summary>
    public int MaxTicksPerFrame { get; }

    /// <summary>Ticks scheduled since the clock started. The sim's frame number.</summary>
    public long TickCount { get; private set; }

    /// <summary>Simulated seconds elapsed — exactly <see cref="TickCount"/> ticks' worth.</summary>
    public double SimTime => TickCount * TickDelta;

    /// <summary>
    /// How far into the next tick the accumulator stands, 0..1. The view
    /// blends the last two sim states by this, which is what makes movement
    /// smooth at frame rates above the tick rate.
    ///
    /// Clamped, and 1 is reachable: a run of deltas that sums to a whole number
    /// of ticks in real arithmetic can land a hair short in doubles, leaving a
    /// leftover that rounds to 1 in single precision. Blending at 1 just shows
    /// the current state, so it is harmless — but code that assumed a strict
    /// upper bound would be wrong.
    /// </summary>
    public float Alpha { get; private set; }

    /// <summary>
    /// Ticks the clock threw away because the frame hit
    /// <see cref="MaxTicksPerFrame"/>. Non-zero means sim time ran slower than
    /// wall-clock time; it is the number to watch when the sim stops keeping up.
    /// </summary>
    public long DroppedTicks { get; private set; }

    /// <summary>
    /// Books <paramref name="realDelta"/> real seconds and returns how many
    /// whole ticks the caller must now run. Time past
    /// <see cref="MaxTicksPerFrame"/> is <b>discarded</b>, not queued: a frame
    /// that stalls (a breakpoint, a load hitch, a machine too slow for the
    /// world) makes the sim fall behind the wall clock rather than owing an
    /// ever-growing debt it can never pay off. Every tick that does run is
    /// still exactly <see cref="TickDelta"/> long, so the sim stays
    /// deterministic — only its correspondence to real time is lost.
    /// </summary>
    public int Advance(double realDelta)
    {
        if (realDelta > 0.0 && !double.IsNaN(realDelta))
        {
            _accumulator += realDelta;
        }

        int ticks = 0;
        while (_accumulator >= TickDelta && ticks < MaxTicksPerFrame)
        {
            _accumulator -= TickDelta;
            ticks++;
        }

        if (_accumulator >= TickDelta)
        {
            var owed = (long)(_accumulator / TickDelta);
            DroppedTicks += owed;
            _accumulator = Math.Max(0.0, _accumulator - owed * TickDelta);
        }

        TickCount += ticks;
        Alpha = Math.Clamp((float)(_accumulator / TickDelta), 0f, 1f);
        return ticks;
    }
}
