## Labour and wages (`src/sim/LabourPool.cs`)

> **A hired worker is pure cost until the player gives it a vehicle** — the M5
> lesson, not a gap. No job pool and no auto-assignment; #33 is where the
> player types one, and a pool that found its own work would teach the
> opposite.

**A `Node`**, not a plain class like `CropSystem`: a worker is on nothing, so
`WorldGrid` was no owner for it, while the fee and wage want to be inspector
numbers and the `Economy` scene wiring. **Workers are interchangeable** — one
wage, and a row's only column is the day hired; a skill is content nothing can
consume until M6 varies the work. **The boundary is `TotalDays` against a
stored `_lastPaidDay`** ([`## Game calendar`](calendar-random.md)), compared
rather than watched for, so a date jump charges every skipped day and whoever
is on the books when it falls pays a whole one; prorating is a per-worker
accrual nobody needs yet.

**The fee is refusable, the wage is not** — you cannot hire what you cannot pay
for, but a day passing is not a request. Hence **insolvency: the balance floors
at zero and the shortfall becomes `Arrears`**, rolled into the next boundary,
so a debt settles itself once income exists. Rejected: a negative balance (M2
made "never negative" an invariant every placement check reads through
`CanAfford`), auto-firing the unpaid (the sim repairing the player's payroll —
the convenience M5 forbids), and forgiving the day (insolvency free just as it
starts to bite). With no `Economy` wired labour is free, as a tool without one
builds free. What arrears *cost* is M7/M8's — a consequence needs a market to
be one in; the hire UI is M10's, and the pool is finite (four) so hiring is a
decision.
