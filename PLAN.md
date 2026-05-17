# PLAN: Incremental migration to portable C with XP-through-11 support

This plan replaces the .NET Framework service with a small native Win32
service in C, built so it loads and runs on Windows XP and works correctly
through Windows 11. It is staged so the project keeps working at every
step — the C# build stays green until the C build is at parity, then the
C# project is retired.

## Honest scope note about XP

Windows XP has no documented lid-state notification API. The clean
`RegisterPowerSettingNotification` / `GUID_LIDSWITCH_STATE_CHANGE` path
arrived in Vista. Real options on XP:

1. **`WM_POWERBROADCAST` proxy.** Most laptops are configured so lid
   close triggers `PBT_APMSUSPEND` and lid open triggers
   `PBT_APMRESUMEAUTOMATIC` / `PBT_APMRESUMESUSPEND`. This is the
   approach this plan adopts for XP. It only fires when the OEM has
   wired lid to sleep — which on XP-era hardware is the default. If a
   user has set "Do nothing" for lid-close in Power Options, XP will
   produce no signal and there is nothing we can do about it from user
   mode.
2. `RegisterDeviceNotification` with a lid device interface GUID. Works
   on XP but only signals device arrival/removal, not open/close.
3. ACPI driver hooks. Undocumented, kernel-mode, out of scope.

The plan therefore promises "best-effort lid event detection" on XP and
"authoritative lid state" on Vista+. The runtime picks the better source
automatically.

## Goals

1. One small native EXE — runs as a Windows service on XP SP3 through
   Windows 11.
2. No runtime dependency: link `msvcrt.dll` (present since Windows 95)
   so users don't need .NET or a VC redistributable.
3. Fix the existing correctness bugs (SCM handler takeover, GC'd
   delegate, hardcoded log path) along the way.
4. Code is unit-testable without installing as a service; CI runs
   tests on every push.
5. Ships the missing "run user script on lid event" feature.

## Target toolchain

**Recommended: MinGW-w64 (i686) targeting `_WIN32_WINNT=0x0501`.**

- Builds on Linux runners, which we already have for CI.
- Produces 32-bit binaries that load on every Windows from XP to 11
  (Windows 11 still runs 32-bit user-mode binaries via WoW64).
- Links against the system `msvcrt.dll`, no redist needed.
- Single compiler covers every target — no per-OS build matrix.

**Alternative: MSVC v141_xp toolset (VS 2017).** Last MS toolset that
supports XP. Useful if you want WinDbg-quality PDBs, but it ties the
build to a Windows host.

Decision: start with MinGW-w64. Add MSVC as a second CI job later if
PDBs become important.

A 64-bit Vista+/Win7+ build can be added trivially with the same source
once XP is no longer in the matrix; the only thing stopping us from
shipping 64-bit today is XP itself (the last 64-bit XP build is XP x64,
which is rare and best ignored).

## High-level architecture

Source layout under `src/`:

```
src/
  main.c             entry: dispatches install/uninstall/run
  service.c          SCM glue: StartServiceCtrlDispatcher, ServiceMain,
                     control handler, status transitions
  lid_source.h       abstract lid event source (vtable)
  lid_vista.c        Vista+ path: RegisterPowerSettingNotification
  lid_xp.c           XP path: WM_POWERBROADCAST proxy via service
                     control handler (SERVICE_CONTROL_POWEREVENT)
  lid_select.c       runtime: pick lid_vista if available else lid_xp
  runner.h/.c        spawn user script with "opened" or "closed" arg
  log.h              logger interface
  log_eventlog.c     Event Log writer
  log_file.c         %ProgramData%\LidStatusService\log.txt fallback
  config.h/.c        read script path from registry
  installer.c        install / uninstall via CreateService / DeleteService
tests/
  test_main.c        tiny test harness (no external dep)
  test_lid_xp.c
  test_runner.c
  fake_lid_source.c
  fake_logger.c
  fake_spawner.c
```

Every external dependency the service has — lid events, logging,
process spawning, registry reads — sits behind a small struct-of-function-
pointers "interface" with an opaque `void *ctx`. Production builds wire
the real implementations; tests wire fakes. That is the entire
testability strategy in C.

Example shape:

```c
/* lid_source.h */
typedef enum { LID_OPENED, LID_CLOSED } lid_state_t;
typedef void (*lid_callback_t)(void *user, lid_state_t state);

typedef struct lid_source {
    int  (*start)(struct lid_source *self, lid_callback_t cb, void *user);
    void (*stop) (struct lid_source *self);
    void (*free) (struct lid_source *self);
    void *impl;
} lid_source_t;

lid_source_t *lid_source_create_best(SERVICE_STATUS_HANDLE svc);
```

`service.c` only ever talks to `lid_source_t`. It has no idea whether
it's on Vista or XP.

## Fixing the existing bugs by construction

The C rewrite eliminates the .NET-era bugs structurally:

- **SCM handler is owned in exactly one place** (`service.c`), and it
  switch/cases on every control code we need (`STOP`, `SHUTDOWN`,
  `INTERROGATE`, `POWEREVENT`). The lid source receives power events
  via a callback from that handler, not by registering its own.
- **No managed delegate to GC.** Callbacks are plain C function
  pointers; lifetime is the service process.
- **No hardcoded log path.** `log_file.c` resolves
  `SHGetFolderPathW(CSIDL_COMMON_APPDATA)` (XP-safe) and creates
  `LidStatusService\log.txt` under it. `log_eventlog.c` is the default.
- **Explicit teardown** in `ServiceMain` after `WaitForSingleObject`
  on the stop event — no finalizers calling unmanaged code.

## Migration steps

Each phase is independently mergeable and leaves the project in a
working state. The C# build stays the supported binary until phase 7.

### Phase 0 — Stabilize current C# and add CI (1 day)

- Add a GitHub Actions workflow (`windows-latest`) that builds the
  existing `.sln` with msbuild. No tests yet; just turn the build red
  when someone breaks it.
- Fix the two correctness bugs in C# so the current shipping binary
  isn't subtly broken while we work on the rewrite:
  - Pin the `ServiceControlHandlerEx` delegate as an instance field
    on `Lid`.
  - In `MessageHandler`, return `NO_ERROR` for `SERVICE_CONTROL_STOP`
    and call `ServiceBase.Stop()` (or do the SCM transitions
    directly) so the service can actually stop.
- Tag this commit `v0-csharp-baseline`. Useful for A/B comparison
  during the rewrite.

### Phase 1 — Land C scaffolding alongside C# (1–2 days)

- Add `src/` and `tests/` and a `Makefile` (or `CMakeLists.txt`).
- Add a second CI job that runs on Ubuntu, installs `mingw-w64`, and
  builds `bin/lidstatusservice.exe` and `bin/lidstatusservice-tests.exe`.
- The first C program is a no-op service: registers with SCM,
  accepts STOP, exits cleanly. Verify on Windows 7 and Windows 11
  VMs. (XP VM verification deferred to phase 4.)
- The test harness is ~50 lines: a `TEST(name) { ... }` macro that
  pushes into an array, `main()` iterates, prints pass/fail, exits
  non-zero on failure. No external dependency.

### Phase 2 — Implement Vista+ lid source in C (2–3 days)

- Port the working parts of `Lid.cs` to `lid_vista.c`:
  `RegisterPowerSettingNotification(GUID_LIDSWITCH_STATE_CHANGE,
  DEVICE_NOTIFY_SERVICE_HANDLE)`, decode `POWERBROADCAST_SETTING`
  using `DataLength` (don't hardcode one byte).
- Wire it behind `lid_source_t`.
- `lid_select.c` calls `GetProcAddress(user32,
  "RegisterPowerSettingNotification")` and returns the Vista source
  if non-null. (Linking `RegisterPowerSettingNotification` at load
  time would refuse to start on XP — must be `LoadLibrary` +
  `GetProcAddress`.)
- Logging goes to Event Log on Vista+, with the file logger
  available via a `--log-file` flag for debugging.
- Service stops cleanly. Demo on Windows 7, 10, 11.

### Phase 3 — Tests and fakes (1–2 days)

- `fake_lid_source.c`: exposes a `fake_lid_raise(state)` for tests.
- `fake_logger.c`: records every line into a buffer.
- `fake_spawner.c`: records argv arrays.
- Tests cover: lid open → logger sees "opened"; lid close → logger
  sees "closed"; stop request → stop event signalled.
- Tests run in CI under Wine (`wine bin/lidstatusservice-tests.exe`).
  Wine handles this kind of pure user-mode C just fine.

### Phase 4 — XP fallback (3–5 days)

- `lid_xp.c` registers `SERVICE_CONTROL_POWEREVENT` interest and
  interprets `PBT_APMSUSPEND` as "closed", `PBT_APMRESUMEAUTOMATIC`
  (and `PBT_APMRESUMESUSPEND` for older XP) as "opened". This works
  on XP without any Vista-only API.
- `lid_select.c` falls back to this on XP.
- README documents the XP caveat (signal depends on the user's
  power scheme having "sleep on lid close").
- Verify on an XP SP3 VM. This is the only phase that requires an XP
  VM; everything else stays toolchain-independent.

A useful side effect: `lid_xp.c` also works on Vista+ if the user
explicitly forces the XP backend with a `--force-xp-source` flag —
handy for testing the fallback on machines that aren't XP.

### Phase 5 — Script runner (2 days)

- `runner.c` spawns a configurable executable with a single argv:
  `opened` or `closed`. Uses `CreateProcessW`. Tests stub this out
  via `fake_spawner`.
- `config.c` reads
  `HKLM\SOFTWARE\LidStatusService\ScriptPath` (REG_SZ) and
  `...\Timeout` (REG_DWORD). Default script path:
  `%ProgramData%\LidStatusService\on-lid-event.cmd`.
- If the script is missing, log once at start, no-op on each event.
  If it exits non-zero or hangs past timeout, log and move on. The
  service must not get stuck on a broken user script.
- Tests cover: missing script → no spawn; present script → spawn
  with right arg; spawn fails → logged; success → logged.

### Phase 6 — Native installer (1 day)

- `installer.c` adds `LidStatusService.exe install` and
  `LidStatusService.exe uninstall` subcommands using `OpenSCManager`
  / `CreateServiceW` / `DeleteService` / `ChangeServiceConfig2W`.
  No `installutil.exe`, no .NET SDK on the target.
- `install` registers the Event Log source.
- README is updated with the new install steps and removes the
  Visual Studio / Rider requirement.

### Phase 7 — Retire the C# project (0.5 day)

- Delete `LidStatusService.sln`, `LidStatusService/`, `App.config`,
  `Properties/`, `*.resx`.
- Remove the C# CI job.
- README now describes a single artifact.
- Tag `v1-c`.

### Phase 8 — Release artifacts (1 day)

- CI publishes `lidstatusservice-x86.exe` from a tag push.
- Add a SHA256SUMS file and optionally signing if a code-signing
  cert is available (cosmetic on XP, increasingly important on 10/11
  to avoid SmartScreen warnings).

## Compatibility notes baked into the code

- All Win32 API calls use the explicitly-named variants — `CreateProcessW`,
  `RegOpenKeyExW`, `SHGetFolderPathW`. UTF-16 throughout. `setlocale`
  not used.
- No use of `_Ex2` or `..Ex` variants newer than Windows 2000 unless
  guarded by `GetProcAddress`. The only Vista+ symbol we use is
  `RegisterPowerSettingNotification`, and that one is always
  late-bound.
- `_WIN32_WINNT=0x0501` and `WINVER=0x0501` in the makefile. Any
  accidentally-newer API use becomes a compile error.
- Compile with `-Wall -Wextra -Werror` under MinGW. Treat
  `-Wunused-result` as fatal — Win32 APIs return errors silently and
  we want every one checked.
- Run static analysis (`cppcheck`) and a sanitizer-equivalent (Wine
  + Dr. Memory) in CI.

## Testability strategy in detail

Five seams, all the same shape:

```c
typedef struct logger {
    void (*info) (struct logger *, const wchar_t *fmt, ...);
    void (*error)(struct logger *, const wchar_t *fmt, ...);
    void *ctx;
} logger_t;
```

Production wiring lives in `main.c`/`service.c`. Tests wire fakes.
This is the entire dependency-injection story — no framework, no
macros, no headers more than 30 lines.

Tests we want before phase 7:

- `lid_vista`: extract `POWERBROADCAST_SETTING` decode into a pure
  function and test it with crafted byte buffers (covers the
  one-byte-vs-DataLength bug).
- `lid_xp`: feed synthetic control codes / event types into the
  proxy classifier and assert the resulting `lid_state_t`.
- `lid_select`: stub `GetProcAddress` via a function pointer and
  assert the right source is chosen.
- `runner`: missing script, present script, spawn failure, exit
  non-zero, timeout.
- `service`: state machine — verify the right `SERVICE_STATUS`
  sequence on start/stop/shutdown.

All of these run in seconds, on CI, under Wine.

## What this does *not* try to do

- 64-bit binary for XP x64 (rare; defer).
- ARM64 Windows (could add a CI job later; same C source).
- A GUI configurator. Registry + a `.cmd` file is enough and keeps
  the install footprint tiny.
- Hot-reloading config. Restart the service.
- Crash dumps / telemetry. Out of scope until someone asks.

## Estimated effort

About 2 weeks of focused work, end to end, including Windows VM time.
Phases 0–3 are the riskiest because they set the foundation; phases
4–6 are mostly mechanical once the seams are in place.

## Open questions for the human

1. Is XP SP3 acceptable as the floor, or do you want SP2? (SP3 is
   what most of the world ran; SP2 changes some service APIs.)
2. Are you OK with the "lid behavior on XP depends on power scheme"
   caveat in the README?
3. Code-signing cert available for release artifacts, or skip?
4. Should the user script be PowerShell-only (matches current
   README), or any executable? Going with "any executable, you
   choose `powershell.exe -File ...` in your `.cmd`" is simpler and
   works on XP (which doesn't have PowerShell preinstalled).
