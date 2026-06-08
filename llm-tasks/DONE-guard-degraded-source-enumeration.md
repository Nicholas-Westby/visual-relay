# Fail the build/dev-loop loudly on a degraded source-file view

On the Tart / virtio-fs dev VM, the guest's directory-enumeration (`readdir`) cache can go
stale for a repo's subdirectories: the files still exist and read fine *by name*, but listing
the directory returns empty. MSBuild's default `**/*.cs` glob enumerates via `readdir`, so a
project silently compiles only a **subset** (or zero) of its sources — with no hint at the real
cause. This actually happened here:

- `VisualRelay.Core` enumerated 0 of its files and compiled into an empty ~4 KB assembly.
- `VisualRelay.App` / `.Init` / `.RunTask` then failed with cryptic `CS0234: 'X' does not exist
  in the namespace 'Y'` errors (App couldn't see its own `Services`/`ViewModels`/`Views`;
  the tools couldn't see `VisualRelay.Core`).
- Diagnosis was slow: `git ls-files '*.cs'` returned 139 files while `find` saw 74, and an
  incremental `./visual-relay check` had even looked **green** earlier by reusing a stale
  pre-corruption `App.dll` — masking the broken state.

The underlying staleness is environmental (VM/host), **not** a code defect — the fix is to
remount the share (the `claude-vm/fix-cache.sh` umount+remount workaround, or restart the VM;
note a plain `rm -rf obj bin` does **not** help). But the dev loop should **detect** this and
fail with a clear, self-explanatory message instead of emitting empty assemblies and cryptic
`CS0234` cascades.

## What to build

- Add a cheap sanity guard wired into the `build` and `check` cases of `./visual-relay` (a small
  guard script, or an MSBuild target in `Directory.Build.targets` if you want it to cover a bare
  `dotnet build` too). For each project — or for the repo as a whole — compare the number of
  source files **git tracks** against the number **visible on disk** (excluding `obj/`/`bin/`),
  e.g. `git ls-files '*.cs'` vs `find ... -name '*.cs'`. Fail fast when the visible count is
  drastically below the tracked count (0, or below ~50%).
- The failure message must name the likely cause (**stale virtio-fs / `readdir` cache on the
  VM**, or a broken compile-item glob) and the remedy: **remount the share** — the sorcery
  plugin's `claude-vm/fix-cache.sh`, or `sudo diskutil unmount <path>` + `sudo mount -t virtiofs
  <tag> <path>`, or restart the VM — and explicitly that `rm -rf obj bin` will not help.
- Optionally corroborate with a suspiciously tiny build output (e.g. a `Core.dll` of a few KB).
- Keep it fast and non-flaky for normal builds; it must add negligible time when the source view
  is intact.

## Done when

- When a project's visible/enumerated source count is far below its git-tracked count,
  `./visual-relay build` and `./visual-relay check` fail fast with the cause+remedy message,
  instead of producing an empty assembly and a `CS0234` cascade. Covered by a test (e.g. drive
  the guard with a tracked-vs-visible mismatch fixture) or a documented manual repro.
- A normal build/check (full source visible) is unaffected and not meaningfully slowed.
- `./visual-relay check` green; C#/XAML files under 300 lines; Conventional Commit subjects.

## Note

The root cause of the incident that motivated this task was a VM filesystem cache, not the code.
This task is purely a guardrail so the failure mode is obvious and points at its own fix next
time — the same spirit as the test-timeout/`--blame-hang` guidance already in the repo.
