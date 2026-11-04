namespace MineClaw.Hive.Tests;

/// <summary>DepositTraceGoldenTests' dump (generated from the pre-refactor code).</summary>
internal static class DepositTraceGoldenData
{
    public const string Expected = """"""
== already empty
  settle True
  => DepositReport { Empty = True, Remaining = 0, Deposited = 0, Text = inventory empty (deposited 0) }
== stale empty, hidden items
  settle True
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> ok: stored all
  release chest:1,64,2
  settle True
  => DepositReport { Empty = True, Remaining = 0, Deposited = 3, Text = inventory empty (deposited 3) }
== one chest with room
  settle True
  safe? True
  chests
  claim chest:1,64,2@-1 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> ok: stored all
  release chest:1,64,2@-1
  settle True
  => DepositReport { Empty = True, Remaining = 0, Deposited = 74, Text = inventory empty (deposited 74) }
== partial deposits
  settle True
  safe? True
  chests
  claim chest:5,64,2 ttl=60 note=deposit -> True
  deposit (5, 64, 2) -> ok: stored some
  release chest:5,64,2
  safe? True
  chests
  claim chest:5,64,2 ttl=60 note=deposit -> True
  deposit (5, 64, 2) -> ok: stored some
  release chest:5,64,2
  settle True
  => DepositReport { Empty = True, Remaining = 0, Deposited = 74, Text = inventory empty (deposited 74) }
== busy and refused chests
  settle True
  safe? True
  chests
  claim chest:5,64,2 ttl=60 note=deposit -> False
  safe? True
  chests
  claim chest:9,64,2 ttl=60 note=deposit -> True
  deposit (9, 64, 2) -> ok: stored all
  release chest:9,64,2
  settle True
  => DepositReport { Empty = True, Remaining = 0, Deposited = 5, Text = inventory empty (deposited 5) }
== unreachable, step out works
  settle True
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: can't reach chest
  release chest:1,64,2
  safe? True
  chests
  step out -> True
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> ok: stored
  release chest:1,64,2
  settle True
  => DepositReport { Empty = True, Remaining = 0, Deposited = 5, Text = inventory empty (deposited 5); stepped out to reach storage }
== unreachable, no step out, hand-off
  settle True
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no path to chest
  release chest:1,64,2
  safe? True
  chests
  step out -> False
  offer 5 -> 3.5,64,4.5
  toss at 3.5,64,4.5
  confirm -> 5
  settle True
  => DepositReport { Empty = True, Remaining = 0, Deposited = 5, Text = inventory empty (deposited 5); handed 5 to main }
== full storage, no main, new chest
  settle True
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: chest is full
  release chest:1,64,2
  safe? True
  chests
  place chest 'storage' -> True
  deposit (0, 65, 0) -> ok: stored all
  settle True
  => DepositReport { Empty = True, Remaining = 0, Deposited = 5, Text = inventory empty (deposited 5); placed chest at 0,65,0 }
== nothing works until the deadline
  settle False
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  step out -> False
  offer 5 -> null
  place chest 'storage' -> False
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  step out -> False
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> fail: no route
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  settle True
  => DepositReport { Empty = False, Remaining = 5, Deposited = 0, Text = 5 item(s) still held (no storage space) }
== unsafe then ok, settle throws
  settle throws
  safe? False
  safe? False
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> ok: stored all
  release chest:1,64,2
  settle throws
  => DepositReport { Empty = True, Remaining = 0, Deposited = 5, Text = inventory empty (deposited 5) }
== short deadline
  settle True
  safe? False
  safe? False
  settle True
  => DepositReport { Empty = False, Remaining = 5, Deposited = 0, Text = 5 item(s) still held (no storage space) }
== deposit moves nothing, then hand-off twice
  settle True
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> ok: stored nothing
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> null
  place chest 'storage' -> False
  safe? True
  chests
  offer 5 -> null
  safe? True
  chests
  claim chest:1,64,2 ttl=60 note=deposit -> True
  deposit (1, 64, 2) -> ok: stored nothing
  release chest:1,64,2
  safe? True
  chests
  offer 5 -> 0,64,0
  toss at 0,64,0
  confirm -> 2
  settle True
  => DepositReport { Empty = True, Remaining = 0, Deposited = 5, Text = inventory empty (deposited 5); handed 2 to main }
"""""";
}
