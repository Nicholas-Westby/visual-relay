# Harness improvement: stop rebuilding the driver on every CLI `run-task` (enables parallel drains)

Discovered while batch-driving tasks from the CLI: two concurrent `./visual-relay run-task` invocations from the
same clone **collide and one fails** with `CSC error CS2012: Cannot open '.../VisualRelay.Domain.dll' for writing
-- Access to the path is denied` — the second invocation's build tries to overwrite a DLL the first invocation's
running process holds open. It also makes **every** CLI run pay a multi-second rebuild before stage 1.

## Current state (researched)

> **Freshness contract:** locate anchors by the quoted snippet, not line number.

The wrapper dispatches `run-task` through `dotnet run`, which rebuilds the driver every invocation:

```bash
    dotnet run --project "$SCRIPT_DIR/tools/VisualRelay.RunTask/VisualRelay.RunTask.csproj" -- "$@"
```

(search `tools/VisualRelay.RunTask/VisualRelay.RunTask.csproj` in `visual-relay`). Contrast the GUI/init/gen paths,
which already prefer a PUBLISHED binary and only fall back to `dotnet run`:

```bash
HAS_PUBLISHED=0
[[ -x "$PUBLISHED_APP" ]] && HAS_PUBLISHED=1
...
    if (( HAS_PUBLISHED )); then
      exec "$PUBLISHED_APP" "$@"
```

```bash
    if [[ -x "$PUBLISHED_INIT" ]]; then
      exec "$PUBLISHED_INIT" "$@"
```

So `run-task` is the one hot-path command with **no** published/cached binary — it always rebuilds, and concurrent
rebuilds from one clone race on `obj/Debug/.../*.dll`. The GUI drain avoids this because it is a single long-lived
process; the CLI has no such protection.

Impact observed: a real run (`location-categories-view`) was killed instantly by CS2012 when launched while another
`run-task` was mid-flight; the only workaround was to serialize all CLI runs, losing parallelism.

## What to build

Make repeated/concurrent CLI `run-task` invocations **not rebuild the driver**. Two acceptable approaches (pick one;
the first mirrors the codebase's existing convention):

1. **Published RunTask binary (preferred — matches `PUBLISHED_APP`/`PUBLISHED_INIT`/`PUBLISHED_GC`).** Add a
   `PUBLISHED_RUNTASK="$SCRIPT_DIR/run-task/VisualRelay.RunTask"` (or similar) probe; when present and executable,
   `exec "$PUBLISHED_RUNTASK" "$@"` instead of `dotnet run`. Provide the publish step in whatever produces the other
   published binaries (the Homebrew formula / publish script), so a published install never rebuilds. Keep the
   `dotnet run` fallback for source checkouts.
2. **Build-once-then-`--no-build` with a lock.** On first `run-task`, take a per-clone build lock (e.g. `flock` on a
   file under `obj/`), `dotnet build` the RunTask project once, release the lock, then `dotnet run --no-build`. Subsequent
   concurrent invocations skip the build (binary is up to date) and never write the DLL while another process holds it.

Either way: concurrent `run-task` from one clone must not produce CS2012, and the steady-state invocation must not
rebuild when nothing changed. Stay platform-agnostic (the wrapper already supports macOS/Linux).

## Done when
- Two `run-task` invocations launched concurrently from the same clone both run (no CS2012); verify by launching two
  against different target roots and confirming both reach stage 1.
- A second `run-task` with unchanged driver source does not recompile the driver (no `CSC`/build output before
  `run_start`).
- The `dotnet run` fallback still works in a fresh source checkout with no published binary.

## Note
This is a wrapper/build-infrastructure change (bash + csproj publish), not a typical .NET code task with unit tests,
so it may be better implemented directly than driven through the test-gated pipeline. Captured here per the
"create an LLM task for harness issues that block driving" directive.
