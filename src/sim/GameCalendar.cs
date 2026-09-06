using System;

namespace Arable;

/// <summary>
/// The four seasons of a year, in order. The whole of what M4's crops, M7's
/// price series and M9's hazards will branch on — nothing here knows what any
/// of them mean.
/// </summary>
public enum Season
{
    Spring,
    Summer,
    Autumn,
    Winter,
}

/// <summary>
/// The in-game <b>calendar</b>: ticks counted into days, days into seasons,
/// seasons into years.
///
/// <b>Not to be confused with <see cref="SimClock"/>.</b> There are two clocks
/// and they answer different questions:
/// <list type="bullet">
/// <item><see cref="SimClock"/> is the <i>tick scheduler</i> — real seconds in,
/// whole ticks out. It decides <b>when</b> a tick runs.</item>
/// <item><see cref="GameCalendar"/> is the <i>date</i> — ticks in, a day and a
/// season out. It decides <b>what the date is</b> once a tick has run.</item>
/// </list>
/// Nothing here books real time, and nothing in <see cref="SimClock"/> knows
/// what a day is. That is why the player's speed control can multiply how many
/// ticks run per real second without ever changing how many ticks make a day.
///
/// <b>A day is a count of ticks, not a span of seconds.</b> That single choice
/// is what makes "the same number of ticks elapses per simulated day at every
/// speed" true by construction rather than by arithmetic that has to be kept
/// honest: 2× runs the ticks twice as fast, and the day still ends on the same
/// tick index it always did. It is also what keeps the calendar deterministic —
/// a replay that runs the same ticks lands on the same date, whatever frame
/// rate or speed setting produced them.
///
/// <b>The whole of its state is <see cref="Ticks"/></b> — one integer, and
/// everything else on this class is division. That is deliberate: #25 has to
/// hash sim state and M10 has to save it, and an integer is the cheapest thing
/// either can walk. Keeping a cached day/season alongside it would be a second
/// copy that could drift and would have to be re-derived on load anyway.
///
/// A plain class rather than a <c>Node</c>, for the same reason
/// <see cref="SimClock"/> is: the arithmetic is the interesting part, and a
/// test that wants to stand on the last tick of winter should be able to say so
/// instead of waiting a year for it.
/// </summary>
public sealed class GameCalendar
{
    /// <summary>
    /// Ticks in a day: 600, which is 30 seconds at <see cref="SimClock"/>'s
    /// 20 Hz and 10 at the fastest speed step.
    ///
    /// <b>Why 30 seconds.</b> A machine crosses the 97-cell map in roughly that
    /// long (6 units/s over ~194 units), so "a day" is about one haul end to
    /// end — the day is the unit the player already feels, rather than a number
    /// bolted on beside it. It is also short enough that a five-minute playtest
    /// sees ten of them, which is what makes day-scale effects (M7's price
    /// drift, M8's deadlines) observable in a session rather than in a sitting.
    /// 600 factorises hard (2³·3·5²), so a sub-day schedule can land on exact
    /// halves, thirds, quarters, fifths or tenths of a day with no remainder.
    ///
    /// Expect it to move: M4 calls crop cadence the tempo of the whole game,
    /// which is why this is the default of an <c>[Export]</c> and not a
    /// constant anything computes with.
    /// </summary>
    public const int DefaultTicksPerDay = 600;

    /// <summary>
    /// Days in a season: 12 — six minutes at 1×, so a year is 48 days and about
    /// 24 minutes. Twelve divides into 2, 3, 4 and 6, so M4 can cut a growth
    /// curve into whole-day stages however many stages it settles on; a prime
    /// number of days would force a crop to finish mid-day or off-season.
    /// </summary>
    public const int DefaultDaysPerSeason = 12;

    /// <summary>
    /// Seasons in a year. Fixed at four because <see cref="Season"/> names four
    /// and a season's <i>meaning</i> is what M4 and M9 hang behaviour on — a
    /// tunable count would be a tunable set of names, which is a content
    /// decision, not a tempo one. Season <i>length</i> is the tempo knob.
    /// </summary>
    public const int SeasonsPerYear = 4;

    public GameCalendar(
        int ticksPerDay = DefaultTicksPerDay, int daysPerSeason = DefaultDaysPerSeason)
    {
        TicksPerDay = Math.Max(1, ticksPerDay);
        DaysPerSeason = Math.Max(1, daysPerSeason);
    }

    /// <summary>Ticks that make one day. Set once, at construction.</summary>
    public int TicksPerDay { get; }

    /// <summary>Days that make one season.</summary>
    public int DaysPerSeason { get; }

    /// <summary>Days that make one year — four seasons' worth.</summary>
    public int DaysPerYear => DaysPerSeason * SeasonsPerYear;

    /// <summary>
    /// Ticks elapsed on the calendar. <b>The whole of the calendar's state</b>;
    /// every other property below is arithmetic on this number.
    ///
    /// It happens to equal <see cref="SimClock.TickCount"/> in a fresh game,
    /// but it is the calendar's own counter and not an alias of it: a scenario
    /// that opens in autumn, or a save reloaded on day 300, sets this without
    /// touching the tick scheduler's idea of how long the process has run.
    /// </summary>
    public long Ticks { get; private set; }

    /// <summary>Whole days elapsed, counted from zero. <see cref="Day"/> is the one to show.</summary>
    public long TotalDays => Ticks / TicksPerDay;

    /// <summary>How far into the current day the calendar stands, in ticks.</summary>
    public int TicksIntoDay => (int)(Ticks % TicksPerDay);

    /// <summary>How far into the current day, 0..1 — the "time of day" a day/night cycle would want.</summary>
    public float DayProgress => (float)TicksIntoDay / TicksPerDay;

    /// <summary>
    /// Day of the season, <b>1-based</b>: the first day of spring is day 1, not
    /// day 0. Player-facing counts read from one; <see cref="TotalDays"/> is the
    /// zero-based one to do arithmetic with. Mixing them up is off-by-one in
    /// every date the game prints.
    /// </summary>
    public int Day => (int)(TotalDays % DaysPerSeason) + 1;

    /// <summary>Day of the year, 1-based.</summary>
    public int DayOfYear => (int)(TotalDays % DaysPerYear) + 1;

    /// <summary>The season the current day falls in. Play opens in spring.</summary>
    public Season Season => (Season)(int)(TotalDays / DaysPerSeason % SeasonsPerYear);

    /// <summary>Year, 1-based — the first year is year 1.</summary>
    public int Year => (int)(TotalDays / DaysPerYear) + 1;

    /// <summary>
    /// Advances the date by whole ticks. Called once per sim tick from
    /// <see cref="Simulation"/>, <i>before</i> the systems tick, so a system
    /// that reads the date sees the date of the tick it is running rather than
    /// the one before it.
    ///
    /// There is deliberately no "day changed" signal. A system that cares keeps
    /// the day number it last acted on — one integer in its own arrays, which
    /// saves and hashes with the rest of its state — instead of subscribing to
    /// an event that a load, a replay or a date jump would have to re-fire.
    /// </summary>
    public void Advance(int ticks = 1)
    {
        if (ticks > 0)
        {
            Ticks += ticks;
        }
    }

    /// <summary>
    /// Sets the date outright. The save/scenario door — the counterpart of
    /// <c>Economy.SetBalance</c>, and not something game code should reach for
    /// to make time pass. Negative input is clamped to the first tick of the
    /// first day rather than throwing, because the callers are a save file and
    /// an inspector, both of which can be wrong without deserving a crash.
    /// </summary>
    public void SetTicks(long ticks) => Ticks = Math.Max(0L, ticks);

    /// <summary>
    /// Sets the date by calendar terms, taking <paramref name="year"/> and
    /// <paramref name="dayOfSeason"/> 1-based the way <see cref="Year"/> and
    /// <see cref="Day"/> report them. Lands on the first tick of that day.
    /// </summary>
    public void SetDate(int year, Season season, int dayOfSeason)
    {
        long day = ((long)Math.Max(1, year) - 1) * DaysPerYear
            + (long)(int)season * DaysPerSeason
            + Math.Clamp(dayOfSeason, 1, DaysPerSeason) - 1;
        SetTicks(day * TicksPerDay);
    }

    /// <summary>
    /// The date as the HUD shows it. Lower case and centre-dotted like the
    /// other corner readouts, and season-first because the season is the fact
    /// the player plans around; the day number is the detail inside it.
    /// </summary>
    public string Describe() => $"year {Year} · {Name(Season)} · day {Day}/{DaysPerSeason}";

    /// <summary>The season's name as the UI spells it — lower case, like every other readout.</summary>
    public static string Name(Season season) => season switch
    {
        Season.Spring => "spring",
        Season.Summer => "summer",
        Season.Autumn => "autumn",
        Season.Winter => "winter",
        _ => season.ToString().ToLowerInvariant(),
    };
}
