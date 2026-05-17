# LidStatusService Code & Docs Review

Scope: architecture, design, implementation, testability, correctness, ease of
use, and portability across Windows XP through Windows 11.

## TL;DR

The project is small and the upstream `Lid` wrapper is a reasonable idea, but
the current implementation has one show-stopper correctness bug that explains
the open "service won't stop" TODO, plus a latent crash risk from a GC'd
unmanaged callback delegate. It cannot run on XP or Vista today (both the API
and the .NET Framework target rule those out). Testability is essentially zero
because everything is statically wired and `Log` writes to a hardcoded path.
Docs cover the upstream Win32 background well but say nothing about supported
OS / .NET versions or how to actually use the running service.

## Correctness

### 1. `RegisterServiceCtrlHandlerEx` replaces the ServiceBase handler — this is why Stop doesn't work

`Lid.RegisterEventNotifications` (`LidStatusService/Lid.cs:43`) calls
`RegisterServiceCtrlHandlerEx(serviceName, MessageHandler, IntPtr.Zero)`. SCM
registers exactly one HandlerEx per service, and `ServiceBase` already
installed its own when `ServiceBase.Run` started the service. Re-registering
silently replaces it, so:

- `SERVICE_CONTROL_STOP`, `SHUTDOWN`, `INTERROGATE`, `PAUSE`, `CONTINUE` are
  now delivered to `Lid.MessageHandler` (`Lid.cs:77`), which ignores them and
  returns `IntPtr.Zero`. The service therefore never sees an OnStop and the
  SCM hangs until it times out and kills the process.
- This matches the commit log ("Still can't stop", "Add recommended
  boilerplate for start/stop status. Still can't stop.") and the README TODO
  "Stopping service should stop service".

The whole reason the upstream code does this dance is that `ServiceBase` only
surfaces `OnPowerEvent(PowerBroadcastStatus)` and does not pass `lpEventData`
through, so you can't get the lid GUID payload from it. Real fixes, in order
of preference:

1. **Don't take over the handler.** Use reflection to read `ServiceBase`'s
   private `_acceptedCommands`/`_handlerProc` (or the equivalent on the
   target framework) and chain through. Brittle but keeps SCM happy.
2. **Take it over, then delegate non-power events back to `ServiceBase`.**
   For `SERVICE_CONTROL_STOP` etc., call `Stop()`/`SetServiceStatus` yourself
   with the right transitions. This is what the upstream code _intended_ —
   `OnStop` already does the SetServiceStatus dance — but only `OnStop` runs
   if SCM is told the service stopped, which it isn't because nothing in the
   replaced handler triggers it.
3. **Skip `ServiceBase` and write a raw service.** Cleanest, biggest diff.

Whichever path you pick, this is the single most important fix in the
codebase.

### 2. The HandlerEx delegate can be garbage-collected

`MessageHandler` is passed to native code as a `ServiceControlHandlerEx`
delegate (`Lid.cs:48`). The delegate object itself is not stored anywhere —
it's created implicitly from the method group at the call site. As soon as
the JIT-generated thunk loses its only managed reference, GC is free to
collect it. The next callback from SCM then jumps into freed memory and
crashes the service. The same risk applies to anything that holds the
`Lid` instance only weakly.

Fix: keep the delegate alive for the lifetime of the registration, e.g.

```csharp
private ServiceControlHandlerEx _handlerDelegate;
...
_handlerDelegate = MessageHandler;
RegisterServiceCtrlHandlerEx(serviceName, _handlerDelegate, IntPtr.Zero);
```

This is currently working only because the service process is short-lived
relative to a full GC and the optimizer happens not to drop the reference.

### 3. `PowerBroadcastSetting.Data` is one byte; the struct is actually `UCHAR Data[1]` with `DataLength` bytes

`Lid.cs:85` declares `Data` as a single `byte`. For
`GUID_LIDSWITCH_STATE_CHANGE`, `DataLength` is 4 and the value is a `DWORD`
0/1, so reading the first byte happens to be correct on little-endian
Windows. Anyone who copies this struct for another power setting (e.g.
`GUID_BATTERY_PERCENTAGE_REMAINING`, which is also a DWORD) will silently
get the low byte only. Worth a comment or, better, marshalling the length
and slicing `DataLength` bytes via `Marshal.Copy`.

### 4. No error handling around `RegisterPowerSettingNotification`

`Lid.cs:45` ignores the return value. If registration fails, the handle is
`IntPtr.Zero`, no notifications arrive, and the only sign is silence in the
log. At minimum, throw on `IntPtr.Zero` and log `Marshal.GetLastWin32Error()`.

### 5. Finalizer doing P/Invoke

`~Lid()` calls `UnregisterPowerSettingNotification` (`Lid.cs:23–26`). The
finalizer runs on the finalizer thread with no ordering guarantees with
respect to the underlying service shutdown. Idiomatic .NET is
`IDisposable` + a `GC.SuppressFinalize` pattern, with the service's
`OnStop` (and `Dispose(true)`) doing the unregister deterministically.

### 6. `_guidLidSwitchStateChange` is mutably static

Declared `private static Guid` and passed by `ref`. `static readonly` is
safer — readers can copy into a local and pass that by `ref` if needed.

## Architecture & design clarity

- Splitting `Lid` from the service is the right move; `Lid` is the only
  non-trivial piece and isolating P/Invoke makes the service class
  legible.
- The `Lid` API is awkward in two ways:
  - It takes a `serviceHandle` and a `serviceName` and calls
    `RegisterServiceCtrlHandlerEx` itself. That's the abstraction leak
    causing the bug above; `Lid` should not own the SCM handler.
  - The callback is `Action<bool>` where `true` means "opened". The README
    and the `LidEventHandler` consumer both have to know that. The XXX
    comments in `LidStatusService.cs:43–44` already note the better shape:
    `lid.Opened += ...; lid.Closed += ...;` or a `LidState` enum.
- `LidStatusService.cs` mixes three concerns: SCM status transitions,
  lid wiring, and logging. None of them are big enough to need separate
  files, but the `Log` helper should at least move to its own type and
  not live in the service.
- The `LidStatusService` _class_ inside the `LidStatusService` _namespace_
  is the VS template default and works, but it triggers CA1724 and forces
  fully-qualified names if anything else in the namespace ever needs the
  type. Renaming the class (e.g. `LidService`) would be a small clarity
  win.
- `Program.cs` is the standard one-shot. Fine.
- `ProjectInstaller.Designer.cs` is generated by the VS designer; nothing
  remarkable, but the `Description = "Lid Status"` could be more useful
  ("Runs a command when the laptop lid opens or closes").

## Implementation details worth tightening

- `Log` (`LidStatusService.cs:70–76`) opens, writes one line, and closes a
  `StreamWriter` for each event. Cheap given event volume, but the
  hardcoded path `C:\powerstatus.txt` is the worst part:
  - Requires write access to `C:\`. LocalSystem has it, but the moment
    anyone changes the account in the installer this breaks silently.
  - Not portable to systems whose system drive isn't `C:`.
  - Not discoverable.
  Better: write to the Windows Event Log via `EventLog.WriteEntry` (the
  installer can create the source), or at minimum
  `Path.Combine(Environment.GetFolderPath(SpecialFolder.CommonApplicationData), "LidStatusService", "log.txt")`.
- `OnStart` / `OnStop` manually thunk through `SetServiceStatus` even
  though `ServiceBase` already does this. The custom calls are harmless
  here but redundant; remove them once the handler issue (#1) is fixed.
- `ServiceState` enum values use the `Service` prefix
  (`ServiceStopped`...). `Stopped` etc. would read better and match
  `ServiceControllerStatus`.
- `Lid` derives nothing and exposes no events — a future contributor has
  to read the constructor to learn how it works. A short XML doc comment
  on the class would pay for itself.
- Unused references in the csproj (`System.Net.Http`, `System.Management`,
  `System.Xml.Linq`, `System.Data.DataSetExtensions`) are leftover VS
  template noise.

## Testability

Currently zero. Concretely:

- No test project in the solution.
- `Lid` can only be constructed with a real service handle and a real
  service name, and the constructor performs unmanaged side effects.
- `LidEventHandler` is a `private static` method — not reachable from a
  test even if you wanted to feed it synthetic events.
- `Log` is a `private static` writing to a hardcoded path.

Cheapest path to actual tests:

1. Introduce `ILidMonitor` with `event Action<LidState> StateChanged;
   void Start(); void Stop();`. `Lid` implements it; a `FakeLidMonitor`
   raises events on demand.
2. Make `LidStatusService` take an `ILidMonitor` and an `ILogger` via
   constructor injection. `Program.cs` wires the real ones.
3. Write a couple of xUnit tests that drive `FakeLidMonitor.Raise(opened)`
   and assert `ILogger` saw the right line. No SCM involvement needed.

This also enables a console-mode test harness (run the same logic without
installing the service), which is invaluable for the "execute a script"
feature still on the TODO list.

## Ease of use

- README installation is one command (`installutil.exe ...`) — clear, but
  `installutil` is .NET Framework only and frowned upon on modern
  Windows. Consider documenting `sc.exe create LidStatusService
  binPath= "<full path>"` as well, which works without the .NET SDK
  installed on the target machine.
- The "magic location (not yet)" hand-wave in the README is the actual
  product feature; until it ships, the service does nothing the user can
  observe except a file at `C:\powerstatus.txt`. The README should say
  that explicitly.
- No mention of how to uninstall (`installutil /u ...`).
- No release artifact / no GitHub workflow (already on the TODO).
- No way to configure anything (account, command path, log location)
  without rebuilding.

## Documentation

- `README.md`:
  - "Build solution in Visual Studio (or Rider)" — name the .NET
    Framework target so people know they need 4.8 developer pack /
    targeting pack installed.
  - State the minimum supported Windows version (see Portability
    below). This is the TODO "Document OS and library dependencies".
  - Add a section listing what the running service actually does today
    (writes to `C:\powerstatus.txt`) so a user knows what success looks
    like.
- `UPSTREAM-README.md` is a good technical write-up of the Win32 API
  and worth keeping. One small caveat: it cites MSDN URLs that mostly
  redirect to `learn.microsoft.com` now; not a problem, just worth
  noting once.
- No CHANGELOG, no LICENSE file in the repo root. Upstream license
  status is unclear from the fork.
- No architectural diagram, but at this size none is needed.

## Portability: Windows 11 back to XP

Short answer: **Vista is the floor; XP cannot work without a rewrite.**
The current build runs on Windows 7 SP1 and later, and on Windows 11
without changes.

Breakdown:

| Concern | XP | Vista | 7 SP1 | 8/8.1 | 10 | 11 |
|---|---|---|---|---|---|---|
| `.NET Framework 4.8` runtime | no (4.0 max) | no (4.6 max) | yes | yes | yes (in-box) | yes (in-box) |
| `RegisterPowerSettingNotification` (User32) | no | yes | yes | yes | yes | yes |
| `RegisterServiceCtrlHandlerEx` (advapi32) | yes (XP+) | yes | yes | yes | yes | yes |
| `GUID_LIDSWITCH_STATE_CHANGE` | n/a | yes | yes | yes | yes | yes |
| `installutil.exe` workflow | yes | yes | yes | yes | yes | yes (deprecated) |

So:

- **Windows XP**: not viable. `RegisterPowerSettingNotification` was
  introduced in Vista; XP only has `WM_POWERBROADCAST` /
  `PBT_APMRESUMESUSPEND` / `PBT_APMSUSPEND` and no first-class lid
  notification at all (drivers route lid to ACPI sleep). To support XP
  you would need a different detection path entirely (e.g. polling
  `SetupDi*` over the lid device, or hooking power management at the
  driver level) and a build that targets .NET Framework 4.0, which is
  the last version to install on XP. Realistically: drop XP from the
  TODO unless you're prepared to maintain a second backend.
- **Vista RTM/SP1**: `RegisterPowerSettingNotification` is present, but
  .NET 4.8 isn't supported there. Re-targeting to .NET Framework 4.5
  (last to support Vista SP2) brings Vista SP2 in, at the cost of some
  language/runtime features you aren't using here anyway. Vista RTM
  remains out.
- **Windows 7 SP1+ / 8 / 8.1 / 10 / 11**: works as-is once the
  correctness bugs above are fixed. On modern Windows 10/11, the only
  papercut is that `installutil` is deprecated; document `sc.exe`
  install too.
- **Architecture**: `AnyCPU` with `Prefer32Bit=false` runs as 64-bit on
  64-bit Windows and 32-bit on 32-bit Windows. There's nothing in the
  P/Invoke signatures that would care about bitness (handles are
  `IntPtr`, GUIDs are GUIDs, `DWORD` is `int`). The TODO entry "Convert
  to 64-bit" is already effectively done; "Target multiple
  architectures" would only matter if you ship platform-specific
  binaries.
- **Hardcoded `C:\` log path** is not portable across systems where the
  system drive isn't C: (rare, but a real thing on imaged corporate
  fleets and BitLocker-rearranged drives). Use `SpecialFolder` paths.
- **Service account**: installer hardcodes `LocalSystem`. That has
  access to power events and the C drive, but it's overkill — power
  notifications work for any service account that has its own service
  handle. Using `LocalService` would reduce blast radius.

## Suggested order of attack

1. Fix the SCM handler takeover so the service can actually stop.
2. Pin the HandlerEx delegate.
3. Replace `C:\powerstatus.txt` with Event Log or `%ProgramData%` path.
4. Add `ILidMonitor`/`ILogger` seams and a tiny xUnit test project.
5. Replace `Action<bool>` with explicit `Opened`/`Closed` events (or
   `LidState` enum). Update README.
6. Document supported Windows versions in README; remove XP from TODO
   or call out the work involved.
7. Implement the "run script on event" feature against the seams from
   step 4.
8. Add a CI workflow (build + tests + artifact).
