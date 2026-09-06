using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for the world grid and machines: instances Main.tscn and
/// asserts the generated terrain (seeded, bounded, varied, deterministic) and
/// the starting road, that terrain and placement are independent layers,
/// exercises the entity store, spatial hash and RNG streams directly, then spawns a machine
/// via dev key 9 and asserts its sim row drives the road on the fixed tick with
/// the node interpolating behind it, exercises the road-build
/// tool (armed from the build palette, then anchor click + place click →
/// straight road with diagonal steps), and drives known screen pixels
/// through the shared cell picker to check the hover readout (dev key 8). Run
/// with:
/// godot --headless res://scenes/dev/WorldSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
/// </summary>
public partial class WorldSmokeTest : Node
{
    /// <summary>The road tool's place on the build palette — its first entry.</summary>
    private const int RoadEntry = 0;

    /// <summary>
    /// Sim ticks to let the machine drive before checking it moved. Counted in
    /// ticks, not frames: the sim runs on its own clock now, so a frame number
    /// says nothing about how far anything got.
    /// </summary>
    private const long DriveTicks = 60;

    /// <summary>
    /// How long to hold the pause, in <b>real milliseconds</b> rather than
    /// frames. Headless runs hundreds of frames a second, so a frame count
    /// could pause and resume inside a window where no tick was due anyway,
    /// and "pause stops the sim" would pass without ever having been tested.
    /// </summary>
    private const ulong PauseMs = 400;

    private WorldGrid _world = null!;
    private GridMap _gridMap = null!;
    private RoadBuildTool _roadTool = null!;
    private BuildPalette _palette = null!;
    private CellInspector _inspector = null!;
    private Label _readout = null!;
    private Label _moneyReadout = null!;
    private Simulation _sim = null!;
    private TimeControls _time = null!;
    private CameraRig _rig = null!;
    private readonly Dictionary<Machine, Vector3> _startPositions = new();
    private int _frame;
    private bool _failed;
    private bool _sawInterpolatedPose;
    private bool _sawViewBetweenSimStates = true;
    private bool _paused;
    private ulong _pauseStartedMs;
    private long _pausedTickCount;
    private long _pausedCalendarTicks;
    private float _pausedAlpha;
    private Vector3 _pausedRigPosition;

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(main);
        _world = main.GetNode<WorldGrid>("World");
        _gridMap = _world.GetNode<GridMap>("GridMap");
        _roadTool = main.GetNode<RoadBuildTool>("RoadTool");
        _palette = main.GetNode<BuildPalette>("Hud/BuildPalette");
        _inspector = main.GetNode<CellInspector>("CellInspector");
        _readout = main.GetNode<Label>("Hud/CellReadout");
        _moneyReadout = main.GetNode<Label>("Hud/MoneyReadout");
        _sim = main.GetNode<Simulation>("Sim");
        _time = main.GetNode<TimeControls>("Hud/TimeControls");
        _rig = main.GetNode<CameraRig>("CameraRig");
    }

    public override void _Process(double delta)
    {
        _frame++;
        if (_frame == 5)
        {
            CheckTerrain();
            CheckLayersAreIndependent();
            CheckStartRoad();
            CheckSimClock();
            CheckGameCalendar();
            CheckSpeedSchedule();
            CheckEntityStorage();
            CheckRandomStreams();

            // Dev key 9 spawns a machine — one of the two shortcuts left over
            // from the number-key menu the build palette replaced.
            Input.ParseInputEvent(new InputEventAction { Action = "menu_9", Pressed = true });
        }
        else if (_frame == 10)
        {
            foreach (Node node in GetTree().GetNodesInGroup("machines"))
            {
                _startPositions[(Machine)node] = ((Machine)node).Position;
            }
            Check("dev key 9 spawned a machine", _startPositions.Count == 1);
            // One system for every machine, not one system per machine: the
            // sim registration count must not track the entity count.
            Check("the machine registered with the sim",
                _sim.SystemCount == 1 && _world.Machines.Count == 1);
            foreach (Machine machine in _startPositions.Keys)
            {
                Check("the machine node draws a live sim entity",
                    _world.Machines.IsAlive(machine.Entity));
            }

            // The build palette is how a tool is armed: the first entry is the
            // road tool, and Select is the same call its button makes.
            Check("road tool starts inactive", !_roadTool.Active);
            _palette.Select(RoadEntry);
        }
        else if (_frame == 15)
        {
            Check("the palette arms the road tool", _roadTool.Active);

            // First click anchors, second click places a diagonal road
            // (headless has no real cursor, so click the cells directly). The
            // tool validates placement now, so this diagonal runs over cells
            // this seed generates as empty soil; refusal itself is
            // BuildSmokeTest's subject.
            _roadTool.ClickCell(new Vector2I(2, 2));
            Check("first click sets the anchor", _roadTool.Anchor == new Vector2I(2, 2));
            _roadTool.ClickCell(new Vector2I(6, 6));
            Check("second click clears the anchor", _roadTool.Anchor == null);
            Check("second click keeps the tool active", _roadTool.Active);
            Check("road line endpoints were placed",
                _world.IsRoad(new Vector2I(2, 2)) && _world.IsRoad(new Vector2I(6, 6)));
            // A diagonal is stair-stepped into 4-connected cells; the BFS may
            // cut those stair corners, so it walks the road in 4 diagonal
            // steps + the start cell = 5 path cells.
            List<Vector2I>? diagonal = _world.FindRoadPath(new Vector2I(2, 2), new Vector2I(6, 6));
            Check("diagonal road is machine-traversable", diagonal is { Count: 5 });
            // String pulling then collapses it to one straight run.
            Check("diagonal path smooths to a single segment",
                diagonal != null && _world.SmoothRoadPath(diagonal).Count == 2);
            Check("cell beside the new road is empty",
                _world.GetTile(new Vector2I(4, 2)) == TileType.Empty);

            _palette.Toggle(RoadEntry);
        }
        else if (_frame == 20)
        {
            Check("the palette puts the road tool down again", !_roadTool.Active);
            Check("inspector is wired to the world and its label",
                _inspector.World == _world && _inspector.Readout == _readout);
            Check("the readout starts switched on", _inspector.Enabled && _readout.Visible);

            CheckHoverReadout();

            // Dev key 8 toggles the readout (so it can be off for screenshots).
            // It moved off key 2 when the palette took the low number keys for
            // its entries; the readout is a dev instrument, not a build tool.
            Input.ParseInputEvent(new InputEventAction { Action = "menu_8", Pressed = true });
        }
        else if (_frame == 25)
        {
            Check("dev key 8 switches the readout off",
                !_inspector.Enabled && !_readout.Visible);
            Input.ParseInputEvent(new InputEventAction { Action = "menu_8", Pressed = true });
        }
        else if (_frame == 30)
        {
            Check("dev key 8 switches the readout back on",
                _inspector.Enabled && _readout.Visible);

            CheckTimeControls();
            BeginPause();
        }
        else if (_frame > 30)
        {
            if (_paused)
            {
                // The camera is panned throughout the pause; the point of the
                // wait is that real time passes while sim time does not.
                if (Time.GetTicksMsec() - _pauseStartedMs >= PauseMs)
                {
                    EndPause();
                }
                return;
            }

            SampleViewAgainstSimState();
            if (_sim.TickCount >= DriveTicks)
            {
                CheckMachinesDrove();
                GD.Print(_failed ? "SMOKE TEST FAILED" : "SMOKE TEST PASSED");
                GetTree().Quit(_failed ? 1 : 0);
            }
        }
    }

    /// <summary>
    /// The time controls as the player sees them: one button per speed step,
    /// the date on screen, and the panel clear of the rest of the HUD. Like the
    /// palette, the bar is asserted to be a <i>view</i> of the sim -- it is
    /// refreshed and then compared with what the sim says, never with a copy.
    /// </summary>
    private void CheckTimeControls()
    {
        _time.Refresh();
        GD.Print($"time: {_time.DateText}, step {_time.ActiveIndex} of {_time.Count}, "
            + $"speed {_sim.Speed}x, {_sim.Calendar.TicksPerDay} ticks/day, "
            + $"{_sim.Calendar.DaysPerSeason} days/season");

        Check("the calendar was built from the sim's exports",
            _sim.Calendar.TicksPerDay == _sim.TicksPerDay
            && _sim.Calendar.DaysPerSeason == _sim.DaysPerSeason);
        Check("the panel has one button per speed step",
            _time.Count == _sim.Speeds.Count && _time.Count >= 2);
        Check("the first step is the stop and the rest are multipliers",
            _time.Entry(0)?.LabelText == "pause"
            && _time.Entry(0)?.Multiplier == 0f
            && _time.Entry(1)?.LabelText == "1x");
        Check("play opens running, not paused", !_sim.IsPaused && _sim.Speed > 0.0);
        Check("the armed button is the step the sim is on",
            _time.ActiveIndex == _sim.SpeedIndex
            && _time.Entry(_sim.SpeedIndex) is { IsActive: true });
        Check("the readout says exactly what the calendar says",
            _time.DateText == _sim.Calendar.Describe() && _time.DateText.Length > 0);
        Check("the readout names the season and the day",
            _time.DateText.Contains(GameCalendar.Name(_sim.Calendar.Season))
            && _time.DateText.Contains($"day {_sim.Calendar.Day}/"));

        // The panel is a visual claim, so it is checked as one, the way the
        // palette bar is: on the screen, in its own corner, over nothing else.
        Rect2 screen = GetViewport().GetVisibleRect();
        Rect2 panel = _time.Panel.GetGlobalRect();
        GD.Print($"time panel at {panel} on a {screen.Size} screen");
        Check("the time panel is visible", _time.Visible && _time.Panel.Visible);
        Check("the whole panel is on screen", screen.Encloses(panel));
        Check("the panel sits in the bottom-right corner",
            panel.Position.Y > screen.Size.Y * 0.6f
            && panel.GetCenter().X > screen.GetCenter().X);
        Check("the panel does not overlap the build palette",
            !panel.Intersects(_palette.Bar.GetGlobalRect()));
        Check("the panel does not overlap the money readout",
            !panel.Intersects(_moneyReadout.GetGlobalRect()));

        // The button path reaches the clock, at a speed that is not 1.
        int fastest = _sim.Speeds.Count - 1;
        Check("selecting a step puts the clock on that multiplier",
            _time.Select(fastest)
            && _sim.SpeedIndex == fastest
            && Mathf.Abs(_sim.Speed - _sim.Speeds[fastest]) < 0.0001
            && _time.Entry(fastest) is { IsActive: true });
        Check("changing speed never changes the size of a tick",
            Mathf.Abs(_sim.Clock.TickDelta - 1.0 / _sim.Clock.TickRate) < 1e-12);
        Check("an index off the ladder is refused and changes nothing",
            !_time.Select(_sim.Speeds.Count) && _sim.SpeedIndex == fastest);

        _sim.TogglePause();
        Check("toggling pause stops the clock", _sim.IsPaused && _sim.SpeedIndex == 0);
        _sim.TogglePause();
        Check("toggling back returns to the speed it was running at",
            !_sim.IsPaused && _sim.SpeedIndex == fastest);
        _time.Select(1);
        Check("the bar puts it back on 1x", _sim.SpeedIndex == 1 && _sim.Speed == 1.0);
    }

    /// <summary>Pauses from the bar and starts panning, so both halves are watched at once.</summary>
    private void BeginPause()
    {
        Check("the pause button is accepted", _time.Select(0));
        _pausedTickCount = _sim.TickCount;
        _pausedCalendarTicks = _sim.Calendar.Ticks;
        _pausedAlpha = _sim.Alpha;
        _pausedRigPosition = _rig.Position;
        _pauseStartedMs = Time.GetTicksMsec();
        _paused = true;
        Input.ActionPress("camera_forward");
    }

    /// <summary>
    /// What the pause was for: real time passed and the camera moved through
    /// it, while the tick count, the date and even the interpolation alpha
    /// stood still -- a paused world holds its pose rather than creeping on a
    /// stale blend.
    /// </summary>
    private void EndPause()
    {
        ulong held = Time.GetTicksMsec() - _pauseStartedMs;
        GD.Print($"pause: held {held} ms, {_sim.TickCount - _pausedTickCount} ticks ran, "
            + $"camera moved {_rig.Position.DistanceTo(_pausedRigPosition):F2} units");
        Check("the pause lasted long enough that ticks were due",
            held * (ulong)_sim.Clock.TickRate >= 2000);
        Check("pause stops sim ticks", _sim.TickCount == _pausedTickCount);
        Check("pause stops the calendar", _sim.Calendar.Ticks == _pausedCalendarTicks);
        Check("a paused world holds its pose", Mathf.Abs(_sim.Alpha - _pausedAlpha) < 0.0001f);
        Check("the camera still pans while the sim is paused",
            _rig.Position.DistanceTo(_pausedRigPosition) > 0.5f);
        Check("the bar draws pause as the armed step",
            _time.ActiveIndex == 0 && _time.Entry(0) is { IsActive: true });

        Input.ActionRelease("camera_forward");
        _time.Select(1);
        Check("the bar starts time again", !_sim.IsPaused && _sim.SpeedIndex == 1);
        _paused = false;
    }

    /// <summary>
    /// The calendar as plain arithmetic -- days into seasons into years -- on a
    /// deliberately tiny year, so rollovers a real year would take twenty
    /// minutes to reach happen in a handful of ticks.
    /// </summary>
    private void CheckGameCalendar()
    {
        const int TicksPerDay = 10;
        const int DaysPerSeason = 3;
        var calendar = new GameCalendar(TicksPerDay, DaysPerSeason);
        Check("play opens on year 1, spring, day 1",
            calendar.Ticks == 0 && calendar.Year == 1
            && calendar.Season == Season.Spring && calendar.Day == 1);

        calendar.Advance(TicksPerDay - 1);
        Check("the day does not roll a tick early",
            calendar.Day == 1 && calendar.TicksIntoDay == TicksPerDay - 1
            && calendar.DayProgress > 0.8f);

        calendar.Advance();
        Check("the day rolls on exactly the tick that completes it",
            calendar.Ticks == TicksPerDay && calendar.Day == 2
            && calendar.TicksIntoDay == 0 && calendar.TotalDays == 1);

        calendar.Advance(TicksPerDay * (DaysPerSeason - 1));
        Check("days roll over into the next season",
            calendar.Season == Season.Summer && calendar.Day == 1);

        calendar.Advance(TicksPerDay * DaysPerSeason * 3);
        Check("seasons roll over into the next year",
            calendar.Year == 2 && calendar.Season == Season.Spring
            && calendar.Day == 1 && calendar.DayOfYear == 1);

        calendar.SetDate(3, Season.Winter, 2);
        Check("a date can be set outright, the way a save or a scenario would",
            calendar.Year == 3 && calendar.Season == Season.Winter && calendar.Day == 2
            && calendar.TicksIntoDay == 0);
        Check("the readout spells that date out",
            calendar.Describe() == "year 3 · winter · day 2/3");

        calendar.SetTicks(-5);
        Check("a nonsense date is clamped, not thrown",
            calendar.Ticks == 0 && calendar.Year == 1 && calendar.Day == 1);
    }

    /// <summary>
    /// <b>The claim the speed control lives or dies by</b>: a multiplier changes
    /// how many ticks run per real second and nothing else, so a simulated day
    /// is the same number of ticks at every speed -- only sooner. Driven the way
    /// <see cref="Simulation"/> drives it, one calendar tick per scheduled tick,
    /// since that is where the day boundary is actually observed.
    /// </summary>
    private void CheckSpeedSchedule()
    {
        const double Frame = 1.0 / 60.0;
        int ticksPerDay = _sim.Calendar.TicksPerDay;
        double stepSize = -1.0;
        bool sameTicksPerDay = true;
        bool sameStep = true;
        bool realTimeScaled = true;
        var report = new List<string>();

        foreach (float multiplier in _sim.Speeds)
        {
            if (multiplier <= 0f)
            {
                continue;
            }

            var clock = new SimClock { Speed = multiplier };
            var calendar = new GameCalendar(ticksPerDay, _sim.Calendar.DaysPerSeason);
            long rolloverTick = -1;
            double realSeconds = 0.0;
            while (rolloverTick < 0 && realSeconds < 600.0)
            {
                int ticks = clock.Advance(Frame);
                realSeconds += Frame;
                for (int i = 0; i < ticks && rolloverTick < 0; i++)
                {
                    calendar.Advance();
                    if (calendar.TotalDays == 1)
                    {
                        rolloverTick = calendar.Ticks;
                    }
                }
            }

            sameTicksPerDay &= rolloverTick == ticksPerDay;
            sameStep &= stepSize < 0.0 || Mathf.Abs(clock.TickDelta - stepSize) < 1e-12;
            stepSize = clock.TickDelta;
            // A day is ticksPerDay / TickRate simulated seconds, so at n x it
            // has to arrive in about an n-th of that, give or take a frame.
            double expected = ticksPerDay / (double)clock.TickRate / multiplier;
            realTimeScaled &= Mathf.Abs(realSeconds - expected) < Frame * 2.0;
            report.Add($"{multiplier}x: day 2 at tick {rolloverTick} "
                + $"after {realSeconds:F2} real s");
        }

        GD.Print("speed: " + string.Join("; ", report));
        Check($"a day is {ticksPerDay} ticks at every speed", sameTicksPerDay);
        Check("no speed changes the size of a tick", sameStep);
        Check("a higher speed reaches the same day in proportionally less real time",
            realTimeScaled);

        // Pause is the zero step of the same ladder, not a separate mechanism.
        var stopped = new SimClock { Speed = 0.0 };
        int ran = 0;
        for (int i = 0; i < 600; i++)
        {
            ran += stopped.Advance(Frame);
        }
        Check("a paused clock schedules nothing however long it is fed",
            ran == 0 && stopped.TickCount == 0 && stopped.DroppedTicks == 0
            && stopped.IsPaused);
        stopped.Speed = -3.0;
        Check("a negative speed is clamped to paused rather than running backwards",
            stopped.IsPaused && stopped.Speed == 0.0);
    }

    /// <summary>
    /// The tick schedule itself, exercised as plain arithmetic — the one part
    /// of the split a headless run cannot show by changing its own frame rate.
    /// The same total real time, delivered in wildly different frame sizes,
    /// must advance the clock by the same amount of sim time; that is what
    /// "fixed timestep, independent of frame rate" means.
    ///
    /// Measured as ticks *plus* alpha, not ticks alone: a second delivered as
    /// thirty 1/30 s doubles sums a hair under 1.0, so the honest answer is
    /// "19 ticks and 0.9999 of the next", and an exact-equality test on the
    /// tick count alone would call that a failure.
    /// </summary>
    private void CheckSimClock()
    {
        Check("the sim tick rate is not the physics tick rate",
            _sim.Clock.TickRate == 20 && _sim.Clock.TickRate != (int)Engine.PhysicsTicksPerSecond);

        (long Ticks, double Advanced) fast = AdvanceBy(Frames(1.0 / 300.0, 300));
        (long Ticks, double Advanced) slow = AdvanceBy(Frames(1.0 / 30.0, 30));
        (long Ticks, double Advanced) jittery = AdvanceBy(new[]
        {
            0.004, 0.031, 0.007, 0.058, 0.019, 0.003, 0.041, 0.137, 0.200, 0.090, 0.160,
            0.150, 0.100,
        });
        GD.Print($"clock: one second = {fast.Ticks} ticks at 300 fps, "
            + $"{slow.Ticks} at 30 fps, {jittery.Ticks} on jittery frames");
        Check("one simulated second advances the clock 20 ticks at any frame rate",
            Mathf.Abs(fast.Advanced - 20.0) < 0.001
            && Mathf.Abs(slow.Advanced - 20.0) < 0.001
            && Mathf.Abs(jittery.Advanced - 20.0) < 0.001);
        Check("frame rate does not change how many whole ticks run",
            fast.Ticks == 20 && jittery.Ticks == 20 && slow.Ticks >= 19);

        // Spiral guard: a stalled frame runs the cap and throws the rest away,
        // so the next frame is an ordinary one rather than 195 ticks of
        // catch-up that would stall the next frame in turn.
        var stalled = new SimClock();
        int burst = stalled.Advance(10.0);
        int next = stalled.Advance(1.0 / 60.0);
        Check($"a stalled frame runs at most {stalled.MaxTicksPerFrame} ticks",
            burst == stalled.MaxTicksPerFrame);
        Check("time past the cap is discarded, not owed",
            stalled.DroppedTicks > 190 && next <= 1);
    }

    /// <summary>
    /// The entity store and the spatial hash, driven directly rather than
    /// through a machine: like the clock, they are plain classes, so the cases
    /// worth pinning — a recycled slot, a stale handle, an emptied bucket — can
    /// be produced on demand instead of waited for.
    /// </summary>
    private void CheckEntityStorage()
    {
        var store = new EntityStore();
        EntityId first = store.Create();
        EntityId second = store.Create();
        Check("fresh handles are alive and distinct",
            store.IsAlive(first) && store.IsAlive(second) && first != second && store.Count == 2);
        Check("the default handle is never alive", !store.IsAlive(EntityId.None));

        Check("destroying frees the entity",
            store.Destroy(first) && !store.IsAlive(first) && store.Count == 1);
        Check("destroying a stale handle does nothing", !store.Destroy(first));

        EntityId reused = store.Create();
        Check("a freed slot is recycled rather than grown past",
            reused.Index == first.Index && store.SlotCount == 2 && store.IsAlive(reused));
        Check("the stale handle does not address its replacement",
            reused != first && !store.IsAlive(first));

        int walked = 0;
        for (int i = 0; i < store.SlotCount; i++)
        {
            if (store.IsAliveSlot(i))
            {
                walked++;
            }
        }
        Check("a slot walk reaches every live entity", walked == store.Count);

        var hash = new SpatialHash();
        var found = new List<EntityId>();
        var cell = new Vector2I(3, 4);
        hash.Insert(cell, reused);
        hash.Insert(cell, second);
        hash.Query(cell, 0, found);
        Check("the hash answers the cell it filed under", found.Count == 2);

        found.Clear();
        hash.Query(cell + new Vector2I(1, 1), 1, found);
        Check("a neighbourhood query reaches the cells around it", found.Count == 2);

        found.Clear();
        hash.Query(new Vector2I(20, 20), 2, found);
        Check("a query away from everything finds nothing", found.Count == 0);

        hash.Move(cell, cell + Vector2I.Down, second);
        found.Clear();
        hash.Query(cell, 0, found);
        Check("moving an entity re-files it", found.Count == 1 && found[0] == reused);

        hash.Remove(cell, reused);
        hash.Remove(cell + Vector2I.Down, second);
        Check("emptied cells are dropped from the index", hash.OccupiedCells == 0);
    }

    /// <summary>
    /// The RNG streams, and in particular the claim the whole design rests on:
    /// <b>a draw in one system cannot move another system's sequence</b>. That
    /// is checked by building two registries on the same world seed, crowding
    /// one of them with systems that do not exist yet and draining one of those,
    /// and requiring the stream under test to produce the identical sequence in
    /// both. A shared pool fails it on the first draw.
    ///
    /// The rest is what M10 needs: a stream resumes from one saved integer at
    /// the point it was saved, not at the start.
    /// </summary>
    private void CheckRandomStreams()
    {
        const int worldSeed = 20260904;
        const int draws = 32;

        // The derivation is a contract, not an implementation detail: changing
        // it silently changes every world that was ever generated. These are
        // golden values, so moving them has to be a decision somebody makes on
        // purpose rather than a side effect of tidying the hash.
        GD.Print($"rng: hash(machines)={RandomStream.HashName(MachineSystem.StreamName):X16} "
            + $"seed64={RandomStream.Seed64(worldSeed, MachineSystem.StreamName):X16}");
        Check("the stream derivation is unchanged",
            RandomStream.HashName(MachineSystem.StreamName) == 0x27B77DFCCA1759B3UL
            && RandomStream.Seed64(worldSeed, MachineSystem.StreamName) == 0x21034AF9D3EC7C54UL);

        // Two registries on one seed. The busy one opens two systems that do
        // not exist yet and drains one of them before it ever asks for the
        // stream under test -- exactly the change that shifts every later roll
        // when randomness comes out of a shared pool.
        var plain = new RandomStreams(worldSeed);
        var busy = new RandomStreams(worldSeed);
        RandomStream weather = busy.For("weather");
        for (int i = 0; i < 500; i++)
        {
            weather.NextUInt();
        }
        busy.For("prices");

        uint[] alone = Draw(plain.For(MachineSystem.StreamName), draws);
        uint[] crowded = Draw(busy.For(MachineSystem.StreamName), draws);
        Check("draws in other systems do not move this system's sequence",
            SameDraws(alone, crowded));
        Check("and neither does the order the streams were opened in",
            busy.Ordered.Count == 3 && plain.Count == 1);

        Check("two systems in one world get different sequences",
            !SameDraws(alone, Draw(plain.For("weather"), draws)));
        Check("the same system in another world gets a different sequence",
            !SameDraws(alone, Draw(new RandomStreams(worldSeed + 1)
                .For(MachineSystem.StreamName), draws)));

        // Save/load's half of the deal: the whole of a stream is one integer,
        // and restoring it has to resume mid-sequence.
        var saved = new RandomStreams(worldSeed);
        RandomStream stream = saved.For(MachineSystem.StreamName);
        uint[] fromStart = Draw(stream, draws);
        ulong state = stream.State;
        uint[] next = Draw(stream, draws);
        stream.State = state;
        Check("a stream resumes mid-sequence from its saved state",
            SameDraws(next, Draw(stream, draws)));
        Check("resuming mid-sequence is not the same as starting over",
            !SameDraws(next, fromStart));
        Check("a stream is the same object every time it is asked for",
            ReferenceEquals(stream, saved.For(MachineSystem.StreamName)));

        saved.Reseed(worldSeed);
        Check("reseeding rewinds a stream somebody is already holding",
            SameDraws(fromStart, Draw(stream, draws)));

        // The walk #25 hashes and M10 saves: ordinal by name, so it cannot
        // depend on which system happened to draw first.
        var forward = new RandomStreams(worldSeed);
        var backward = new RandomStreams(worldSeed);
        forward.For("aaa");
        forward.For("mmm");
        forward.For("zzz");
        backward.For("zzz");
        backward.For("mmm");
        backward.For("aaa");
        bool sameWalk = forward.Count == backward.Count;
        for (int i = 0; i < forward.Count && sameWalk; i++)
        {
            sameWalk = forward.Ordered[i].Name == backward.Ordered[i].Name
                && forward.Ordered[i].State == backward.Ordered[i].State;
        }
        Check("streams walk by name, not by which one was opened first", sameWalk);

        bool inRange = true;
        RandomStream range = plain.For("range");
        for (int i = 0; i < 4096; i++)
        {
            int below = range.NextInt(7);
            int between = range.NextInt(-3, 4);
            float unit = range.NextFloat();
            inRange &= below is >= 0 and < 7 && between is >= -3 and < 4
                && unit >= 0f && unit < 1f;
        }
        Check("draws stay inside the range they were asked for", inRange);
        Check("a degenerate range answers the only value in it",
            range.NextInt(1) == 0 && range.NextInt(0) == 0);

        // The proof above is worth nothing if the game wired its own registry:
        // the world and the machines have to be drawing from the sim's.
        Check("the world and the machines draw from the sim's registry",
            _sim.Streams.Has(WorldGrid.SpawnStreamName)
            && _sim.Streams.Has(MachineSystem.StreamName));
        int worldsSeed = _world.WorldSeed;
        Check("the world reads its seed from the sim's registry",
            worldsSeed == _sim.Streams.WorldSeed);
        _world.WorldSeed = worldsSeed + 1;
        Check("and setting it reseeds that registry rather than a copy",
            _sim.Streams.WorldSeed == worldsSeed + 1);
        _world.WorldSeed = worldsSeed;
    }

    private static uint[] Draw(RandomStream stream, int count)
    {
        var values = new uint[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = stream.NextUInt();
        }
        return values;
    }

    private static bool SameDraws(uint[] a, uint[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    private static double[] Frames(double delta, int count)
    {
        var frames = new double[count];
        for (int i = 0; i < count; i++)
        {
            frames[i] = delta;
        }
        return frames;
    }

    /// <summary>
    /// Runs a fresh clock through the frames and reports both the whole ticks
    /// it ran and the sim time it advanced, in ticks (whole ticks + alpha).
    /// </summary>
    private static (long Ticks, double Advanced) AdvanceBy(double[] frames)
    {
        var clock = new SimClock();
        long ticks = 0;
        foreach (double frame in frames)
        {
            ticks += clock.Advance(frame);
        }
        return (ticks, ticks + clock.Alpha);
    }

    /// <summary>
    /// The ownership rule, watched every frame: the node transform is a view of
    /// the sim state, so it must always sit on the segment between the last two
    /// sim positions — and at least once must sit strictly between them, which
    /// is the frame that could not have happened without interpolation.
    /// </summary>
    private void SampleViewAgainstSimState()
    {
        foreach (Machine machine in _startPositions.Keys)
        {
            Vector3 previous = machine.PreviousSimPosition;
            Vector3 current = machine.SimPosition;
            float span = previous.DistanceTo(current);
            if (span < 0.0001f)
            {
                continue;
            }
            float detour = machine.Position.DistanceTo(previous)
                + machine.Position.DistanceTo(current) - span;
            _sawViewBetweenSimStates &= detour < 0.001f;
            _sawInterpolatedPose |= machine.Position.DistanceTo(current) > 0.001f;
        }
    }

    private void CheckMachinesDrove()
    {
        GD.Print($"sim: {_sim.TickCount} ticks over {_frame} frames, "
            + $"{_sim.Clock.DroppedTicks} dropped");
        Check("the sim ran on its own clock, not once per frame",
            _sim.TickCount >= DriveTicks && _sim.TickCount < _frame);
        Check("the sim kept up without dropping ticks", _sim.Clock.DroppedTicks == 0);
        foreach ((Machine machine, Vector3 start) in _startPositions)
        {
            Check($"{machine.Name} moved", machine.SimPosition.DistanceTo(start) > 1f);
            Check($"{machine.Name} is on a road",
                _world.IsRoad(_world.WorldToCell(machine.SimPosition)));

            // The spatial index followed it across the cells it drove through,
            // which a rebuild-free index only does if every move re-files.
            Vector2I cell = _world.WorldToCell(machine.SimPosition);
            var here = new List<EntityId>();
            _world.Machines.Occupancy.Query(cell, 0, here);
            Check($"{machine.Name} is indexed on the cell it drove to",
                _world.Machines.CellOf(machine.Entity) == cell
                && here.Count == 1 && here[0] == machine.Entity);
        }
        Check("the sim ran again after the pause", _sim.TickCount > _pausedTickCount);
        Check("the view stays between the last two sim states", _sawViewBetweenSimStates);
        Check("the view draws poses between ticks", _sawInterpolatedPose);
    }

    /// <summary>
    /// Hover readout and the cell picking behind it. Headless has no cursor, so
    /// instead of moving a mouse the test projects a known cell center to its
    /// pixel and drives that pixel back through the picker — a round trip that
    /// fails if either half of screen ↔ cell drifts.
    /// </summary>
    private void CheckHoverReadout()
    {
        Camera3D? camera = GetViewport().GetCamera3D();
        Check("a camera is available to pick through", camera != null);
        if (camera == null)
        {
            return;
        }

        // Cells on screen at the default camera pose, plus one off the map.
        var probes = new[] { Vector2I.Zero, new Vector2I(4, -3), new Vector2I(-6, 5) };
        foreach (Vector2I cell in probes)
        {
            Vector2 pixel = camera.UnprojectPosition(_world.CellToWorld(cell));
            Check($"picker turns the pixel of cell {cell.X},{cell.Y} back into it",
                CellPicker.CellAt(_world, camera, pixel) == cell);
            Check($"inspector reads cell {cell.X},{cell.Y} at that pixel",
                _inspector.Inspect(pixel) == cell);
            Check($"road tool picks cell {cell.X},{cell.Y} at the same pixel",
                _roadTool.PickCell(pixel) == cell);
            Check($"the label shows what the inspector read for {cell.X},{cell.Y}",
                _readout.Text == _inspector.Text && _readout.Text.Length > 0);
        }

        // The readout names all four facts: coords, terrain, fertility, tile.
        // The origin is on the starting road, and generation carved that strip
        // to soil, so its expected content is known.
        string origin = _inspector.Describe(Vector2I.Zero);
        GD.Print("readout at 0,0: " + origin.Replace('\n', ' '));
        Check("readout names the cell coordinates", origin.Contains("cell: 0, 0"));
        Check("readout names the terrain", origin.Contains("terrain: soil"));
        Check("readout names the fertility", origin.Contains("fertility: "
            + _world.GetFertility(Vector2I.Zero)
                .ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));
        Check("readout names the placed tile", origin.Contains("tile: road"));

        // Moving the cursor changes the readout: a neighbouring cell off the
        // road reads as empty rather than repeating the road cell.
        Check("readout follows the cursor to another cell",
            _inspector.Describe(new Vector2I(0, 3)).Contains("cell: 0, 3"));
        Check("an unbuilt cell reads as empty",
            _inspector.Describe(new Vector2I(0, 3)).Contains("tile: empty"));

        // Off the map edge: still a cell, but the terrain layer says so.
        var beyond = new Vector2I(_world.MapSize / 2 + 5, 0);
        Vector2 beyondPixel = camera.UnprojectPosition(_world.CellToWorld(beyond));
        Check("picking past the map edge still resolves the cell",
            _inspector.Inspect(beyondPixel) == beyond);
        Check("readout reports off-map cells sensibly",
            _inspector.Text.Contains($"cell: {beyond.X}, 0")
            && _inspector.Text.Contains("off the map")
            && _inspector.Text.Contains("fertility: -"));
    }

    /// <summary>
    /// The generated terrain layer: bounded, varied (soil + rock + water),
    /// fertility only on soil, and reproducible from the world seed.
    /// </summary>
    private void CheckTerrain()
    {
        int half = _world.MapSize / 2;
        Check("map generated with an odd, non-empty extent",
            _world.MapSize > 1 && _world.MapSize % 2 == 1);
        Check("terrain fills every cell of the map",
            _gridMap.GetUsedCells().Count == _world.CellCount);

        // Bounds: in-bounds cells carry terrain, out-of-bounds ones are
        // distinguishable without a second call.
        Check("map corner is in bounds", _world.InBounds(new Vector2I(half, half))
            && _world.GetTerrain(new Vector2I(half, half)) != TerrainType.OutOfBounds);
        var beyond = new Vector2I(half + 1, 0);
        Check("cell past the edge is out of bounds", !_world.InBounds(beyond)
            && _world.GetTerrain(beyond) == TerrainType.OutOfBounds
            && _world.GetFertility(beyond) == 0f);

        int soil = 0, rock = 0, water = 0;
        float minFertility = float.MaxValue, maxFertility = float.MinValue;
        bool fertilityInRange = true, barrenGroundHasNoFertility = true;
        for (int z = -half; z <= half; z++)
        {
            for (int x = -half; x <= half; x++)
            {
                var cell = new Vector2I(x, z);
                float fertility = _world.GetFertility(cell);
                fertilityInRange &= fertility is >= 0f and <= 1f;
                switch (_world.GetTerrain(cell))
                {
                    case TerrainType.Soil:
                        soil++;
                        minFertility = Mathf.Min(minFertility, fertility);
                        maxFertility = Mathf.Max(maxFertility, fertility);
                        break;
                    case TerrainType.Rock:
                        rock++;
                        barrenGroundHasNoFertility &= fertility == 0f;
                        break;
                    case TerrainType.Water:
                        water++;
                        barrenGroundHasNoFertility &= fertility == 0f;
                        break;
                    default:
                        Check($"cell {cell} has terrain", false);
                        return;
                }
            }
        }

        GD.Print($"terrain: {soil} soil, {rock} rock, {water} water "
            + $"of {_world.CellCount} cells; fertility {minFertility:F3}..{maxFertility:F3}");
        Check("rock cells exist", rock > 0);
        Check("water cells exist", water > 0);
        Check("most of the map is soil", soil > rock + water);
        Check("fertility stays within 0..1", fertilityInRange);
        Check("fertility varies across the map", maxFertility - minFertility > 0.2f);
        Check("rock and water carry no fertility", barrenGroundHasNoFertility);

        // The starting road has to sit on legal ground.
        bool roadStripIsSoil = true;
        for (int x = -16; x <= 16; x++)
        {
            roadStripIsSoil &= _world.IsSoil(new Vector2I(x, 0));
        }
        Check("the starting road strip is soil", roadStripIsSoil);

        CheckSeedDeterminism();
    }

    /// <summary>
    /// Same seed in, same map out — including after the world has generated a
    /// different seed in between.
    /// </summary>
    private void CheckSeedDeterminism()
    {
        (TerrainType[] terrain, float[] fertility) first = Snapshot();

        _world.GenerateTerrain();
        (TerrainType[] terrain, float[] fertility) second = Snapshot();
        Check("regenerating the same seed yields identical terrain",
            SameTerrain(first.terrain, second.terrain));
        Check("regenerating the same seed yields identical fertility",
            SameFertility(first.fertility, second.fertility));

        int seed = _world.WorldSeed;
        _world.WorldSeed = seed + 1;
        _world.GenerateTerrain();
        (TerrainType[] terrain, float[] fertility) other = Snapshot();
        Check("a different seed yields a different map",
            !SameTerrain(first.terrain, other.terrain)
            || !SameFertility(first.fertility, other.fertility));

        _world.WorldSeed = seed;
        _world.GenerateTerrain();
        (TerrainType[] terrain, float[] fertility) again = Snapshot();
        Check("returning to the seed restores the same terrain",
            SameTerrain(first.terrain, again.terrain));
        Check("returning to the seed restores the same fertility",
            SameFertility(first.fertility, again.fertility));
    }

    /// <summary>
    /// Terrain and placement are separate layers: building on a cell and then
    /// clearing it leaves the terrain — and the terrain the view draws —
    /// exactly as generated.
    /// </summary>
    private void CheckLayersAreIndependent()
    {
        var cell = new Vector2I(5, 5);
        var mapCell = new Vector3I(cell.X, 0, cell.Y);
        TerrainType terrain = _world.GetTerrain(cell);
        float fertility = _world.GetFertility(cell);
        int terrainItem = _gridMap.GetCellItem(mapCell);
        Check("test cell starts unbuilt", _world.GetTile(cell) == TileType.Empty
            && terrain != TerrainType.OutOfBounds
            && terrainItem != GridMap.InvalidCellItem);

        _world.SetTile(cell, TileType.Road);
        Check("placing a road does not change the terrain under it",
            _world.GetTerrain(cell) == terrain && _world.GetFertility(cell) == fertility);
        Check("a placed road hides the terrain in the view",
            _gridMap.GetCellItem(mapCell) != terrainItem);

        _world.SetTile(cell, TileType.Empty);
        Check("clearing the tile leaves the terrain intact",
            _world.GetTerrain(cell) == terrain && _world.GetFertility(cell) == fertility);
        Check("clearing the tile shows the terrain again",
            _gridMap.GetCellItem(mapCell) == terrainItem);
    }

    /// <summary>The starting road, on top of the generated terrain.</summary>
    private void CheckStartRoad()
    {
        // The starting road is the single row z = 0, x = -16..16 (33 cells).
        Check("only the starting road was placed", _world.RoadCellCount == 33);
        Check("origin cell is road", _world.IsRoad(Vector2I.Zero));
        Check("road spans to both ends", _world.IsRoad(new Vector2I(-16, 0))
            && _world.IsRoad(new Vector2I(16, 0)));
        Check("cell off the road is empty", _world.GetTile(new Vector2I(0, 1)) == TileType.Empty);
        Check("no machines at start", GetTree().GetNodesInGroup("machines").Count == 0
            && _world.Machines.Count == 0 && _world.Machines.SlotCount == 0);
    }

    private (TerrainType[] Terrain, float[] Fertility) Snapshot()
    {
        int half = _world.MapSize / 2;
        var terrain = new TerrainType[_world.CellCount];
        var fertility = new float[_world.CellCount];
        int i = 0;
        for (int z = -half; z <= half; z++)
        {
            for (int x = -half; x <= half; x++)
            {
                var cell = new Vector2I(x, z);
                terrain[i] = _world.GetTerrain(cell);
                fertility[i] = _world.GetFertility(cell);
                i++;
            }
        }
        return (terrain, fertility);
    }

    private static bool SameTerrain(TerrainType[] a, TerrainType[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    // Bit-exact: the same seed runs the same noise through the same arithmetic,
    // so anything less than equality would hide a determinism bug.
    private static bool SameFertility(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"{(ok ? "PASS" : "FAIL")}: {what}");
        _failed |= !ok;
    }
}
