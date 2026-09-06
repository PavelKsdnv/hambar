namespace Arable;

/// <summary>
/// Walks the whole simulation into one 64-bit number.
///
/// <b>The order of the walk is fixed and depends on nothing incidental.</b>
/// The clock and the calendar are single integers; RNG streams are walked by
/// <see cref="RandomStreams.Ordered"/>, which is sorted by name rather than by
/// which system drew first; and the registered state sources are folded
/// commutatively by name (<see cref="StateHash.WriteUnordered"/>), so the order
/// they registered in — which a load or a differently ordered scene can change
/// — cannot move the result.
///
/// <b>What is deliberately not in it.</b> <c>Speed</c>, the accumulator behind
/// <see cref="SimClock.Alpha"/> and <see cref="SimClock.DroppedTicks"/> are all
/// functions of <i>real</i> time: the same run watched at 1x and at 3x, or on a
/// machine that stuttered, is the same sequence of ticks and must hash the
/// same. Hashing them would make the harness fail on a slow frame, which is the
/// fastest way to get a determinism check switched off. <see cref="SimClock.TickCount"/>
/// is in, because "how far has this world run" is state.
///
/// Both callers of this are meant to work: <c>SimSmokeTest</c> takes it every
/// tick of two runs and reports the first tick they differ on, and M10 takes it
/// either side of a save/load cycle. That is why it takes a
/// <see cref="Simulation"/> rather than anything test-shaped.
/// </summary>
public static class SimStateHash
{
    /// <summary>The hash of everything the simulation currently holds.</summary>
    public static ulong Of(Simulation sim)
    {
        var hash = new StateHash();
        Write(hash, sim);
        return hash.Value;
    }

    /// <summary>
    /// Writes the simulation into an existing hash — the door for a caller
    /// hashing sim state together with something else (M10 hashing a save
    /// stream's header alongside it).
    /// </summary>
    public static void Write(StateHash hash, Simulation sim)
    {
        // The schedule: how far the world has run, and the tick size that says
        // what a tick meant. Not the accumulator, and not the speed.
        hash.Write(sim.Clock.TickCount);
        hash.Write(sim.Clock.TickRate);

        // The date is one long; the tempo it is read against is configuration
        // that changes what every future tick does, so it goes in beside it.
        hash.Write(sim.Calendar.Ticks);
        hash.Write(sim.Calendar.TicksPerDay);
        hash.Write(sim.Calendar.DaysPerSeason);

        // Where every sequence stands. A stream that has drawn one extra number
        // is a world that will diverge on some later tick; this is what makes
        // the harness report it on the tick the draw happened instead.
        hash.Write(sim.Streams.WorldSeed);
        hash.Write(sim.Streams.Count);
        foreach (RandomStream stream in sim.Streams.Ordered)
        {
            hash.Write(stream.Name);
            hash.Write(stream.State);
        }

        // Everything else, folded by name so registration order cannot move it.
        var member = new StateHash();
        ulong fold = 0UL;
        foreach (IHashableState source in sim.States)
        {
            member.Reset();
            member.Write(source.StateName);
            source.HashState(member);
            fold = StateHash.Fold(fold, member.Value);
        }
        hash.WriteUnordered(fold, sim.States.Count);
    }
}
