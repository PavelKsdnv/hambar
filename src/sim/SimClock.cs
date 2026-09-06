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
///
/// This is the <i>scheduler</i>, not the calendar: it says when a tick runs,
/// while <see cref="GameCalendar"/> says what date the tick lands on.
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
    private double _speed = 1.0;

    public SimClock(int tickRate = DefaultTickRate,
        int maxTicksPerFrame = DefaultMaxTicksPerFrame)
    {
        TickRate = Math.Max(1, tickRate);
        MaxTicksPerFrame = Math.Max(1, maxTicksPerFrame);
        TickDelta = 1.0 / TickRate;
    }

    /// <summary>
    /// The player's time scale: how much real time each real second books, and
    /// so <b>how many ticks run per real second</b>. 1 is normal, 2 runs the
    /// tick loop twice as often, 0 is paused.
    ///
    /// <b>It scales the schedule, never the step.</b> <see cref="TickDelta"/>
    /// is untouched by it and every tick is the same size at every speed, which
    /// is the whole reason speed control can exist at all in a deterministic
    /// sim: 3× is "the same ticks, sooner", not "bigger ticks". A variable step
    /// would make the result of a run depend on the speed the player happened
    /// to be watching at, and there would be nothing left to hash (#25) or
    /// replay.
    ///
    /// Pausing at 0 leaves the leftover accumulator and <see cref="Alpha"/>
    /// exactly where they were, so unpausing resumes mid-tick instead of
    /// snapping, and a paused world holds still rather than creeping on a stale
    /// blend.
    ///
    /// Negative and NaN are clamped to 0 rather than rejected: the setter's
    /// callers are UI and inspector exports, and a paused game is a safer
    /// answer to a bad number than time running backwards.
    /// </summary>
    public double Speed
    {
        get => _speed;
        set => _speed = double.IsNaN(value) || value < 0.0 ? 0.0 : value;
    }

    /// <summary>Whether the schedule is stopped — no tick will run until <see cref="Speed"/> moves.</summary>
    public bool IsPaused => _speed <= 0.0;

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
    ///
    /// <see cref="Speed"/> multiplies the time booked, so a frame at 3× asks
    /// for three times the ticks. <see cref="MaxTicksPerFrame"/> is
    /// deliberately <i>not</i> scaled with it: the cap is a guard on how much
    /// work one frame may do, which is a real-time quantity, so at 3× it bites
    /// at the frame rate where the machine is already in trouble (below ~12 fps)
    /// and the sim then runs slower than the multiplier promises — visibly, via
    /// <see cref="DroppedTicks"/>. What it never does is change how many ticks
    /// make a day.
    /// </summary>
    public int Advance(double realDelta)
    {
        if (realDelta > 0.0 && !double.IsNaN(realDelta))
        {
            _accumulator += realDelta * _speed;
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
