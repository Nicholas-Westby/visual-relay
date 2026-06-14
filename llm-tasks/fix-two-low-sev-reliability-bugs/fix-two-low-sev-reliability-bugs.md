# Fix two low-severity latent reliability bugs (unbounded WaitForExit + _find_nix set-e trap)

Low-priority reliability follow-up surfaced in code review on 2026-06-13. Neither bug is
reachable in normal production runs today, but both are silent correctness traps that would
cause confusing failures under specific future test scenarios.

## Current state (researched)

### Bug 1 — `AppIconTests.cs:137`: unbounded `process.WaitForExit()`

`tests/VisualRelay.Tests/AppIconTests.cs:137` calls `process.WaitForExit()` with no timeout
argument after spawning `magick identify`:

```
135        process.Start();
136        var stdout = process.StandardOutput.ReadToEnd();
137        process.WaitForExit();
```

If `magick` ever hangs (e.g. on a corrupted ICO, a slow filesystem, or a test double that
never exits), this line blocks indefinitely — exactly the class of unbounded wait that task 08
(`DONE-08-harden-test-suite-against-hangs-and-missing-magick.md`) explicitly hardened the
suite against for the `check` path and the `ShellTestRunner`. The current test already skips
when `magick` is absent from PATH (`AppIconTests.cs:113-120`), but the hang scenario is
unguarded. `magick identify` is fast in practice, so this is latent rather than active.

### Bug 2 — `visual-relay` launcher lines 55/67/115/164: `_find_nix` exit-1 under `set -euo pipefail`

`visual-relay:2` sets `set -euo pipefail`. `_find_nix` (lines 30-41) returns exit 1 when
`_VISUAL_RELAY_FAKE_NO_NIX` is set:

```
31  if [[ -n "${_VISUAL_RELAY_FAKE_NO_NIX:-}" ]]; then
32    return 1
33  fi
```

There are four call sites that use the `local var; var="$(cmd)"` pattern:

- Line 55 (`_ensure_devshell`): `local nix_bin; nix_bin="$(_find_nix)"`
- Line 67 (`_offer_nix_install`): `local nix_bin; nix_bin="$(_find_nix)"`
- Line 115 (`_require_dotnet`): `local nix_bin; nix_bin="$(_find_nix)"`
- Line 164 (`_missing_required_tools`): `local nix_bin; nix_bin="$(_find_nix)"`

Under bash `set -e`, the exit status of a command substitution on the right-hand side of a
`local` declaration is suppressed (bash treats `local` as a built-in that always returns 0),
**but only for the `local` keyword itself**. When `local` and assignment are on the same line
(`local var; var="$(cmd)"`), the assignment is a separate statement and `set -e` does apply to
its exit code. Therefore, when `_VISUAL_RELAY_FAKE_NO_NIX` is set and
`VISUAL_RELAY_NIX_REENTRY` is not set, `_find_nix` returns 1, the assignment at each of those
four lines propagates the non-zero exit under `set -e`, and the script dies before reaching
the `if [[ -z "$nix_bin" ]]` no-nix fallback that would route to `_offer_nix_install`.

In production, `_VISUAL_RELAY_FAKE_NO_NIX` is never set, so this path is unreachable today.
A future test that exercises the no-nix code path (install-offer, dotnet-not-found, etc.)
without also setting `VISUAL_RELAY_NIX_REENTRY=1` would hit an opaque exit 1 instead of the
expected install-offer or error message.

The fix is to make `_find_nix` always return 0 and emit nothing on no-nix, removing the
semantic overloading of exit codes for a function whose callers all check for an empty output
string anyway.

## What to build

Write the failing test first in each case (TDD). Both fixes are independent; they may land in
separate commits or together.

### 1. Bound `process.WaitForExit()` in `AppIconTests.cs:137`

**Test first** — add a test (or parameterised fact) in `AppIconTests.cs` that verifies the
bounded path:

- Introduce a helper or subtest that exercises the timeout branch: spawn a process that sleeps
  forever (e.g. `sleep 9999` or a `Process` whose stdout never closes), call the updated
  `WaitForExit(10000)`, and assert (a) the call returns within ~15 s wall time, (b) the test
  marks itself as failed/skipped with a clear message rather than blocking, and (c) the process
  is killed. This test should fail against the current `WaitForExit()` (no-arg) because it
  blocks.

**Fix** — in `AppIconTests.cs:137`, replace:

```csharp
process.WaitForExit();
```

with:

```csharp
var exited = process.WaitForExit(10_000);
if (!exited)
{
    try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
    Assert.Fail(
        "magick identify did not exit within 10 s — process killed. " +
        "This may indicate a corrupted ICO or a hung ImageMagick process.");
}
```

Note: `process.StandardOutput.ReadToEnd()` (line 136) already drains stdout before
`WaitForExit`, so the bounded overload is safe here — there is no risk of deadlock from
unread buffers blocking the exit.

### 2. Make `_find_nix` always return 0; emit empty on no-nix

**Test first** — add a shell/bats test (or extend the existing launcher test file if one
exists) that:

- Sets `_VISUAL_RELAY_FAKE_NO_NIX=1`, unsets `VISUAL_RELAY_NIX_REENTRY`, invokes
  `./visual-relay` with a non-interactive mode flag (e.g. a command that reaches
  `_offer_nix_install` or `_require_dotnet`'s no-nix branch), and asserts the script does NOT
  exit 1 silently — instead it should either print the install-offer message and exit with a
  known non-zero code (127 for missing tools, or 1 from a declined offer) **and** emit
  actionable output. Confirm the test fails against the current launcher (exits 1 with no
  output because `set -e` fires on the bare assignment before the `if`-check).

**Fix** — in `_find_nix` (lines 30-41), change `return 1` to `return 0`:

```bash
_find_nix() {
  if [[ -n "${_VISUAL_RELAY_FAKE_NO_NIX:-}" ]]; then
    return 0   # emit nothing; callers check for empty output
  fi
  ...
}
```

No changes needed at the four call sites: the pattern `local nix_bin; nix_bin="$(_find_nix)"`
already checks `if [[ -z "$nix_bin" ]]` immediately after, and each function's no-nix branch
falls through correctly once the assignment no longer propagates a non-zero exit.

Verify the fix covers all four call sites:
- `visual-relay:55` (`_ensure_devshell`) — falls through to `return 0` (no nix, proceed)
- `visual-relay:67` (`_offer_nix_install`) — falls through to install-offer logic
- `visual-relay:115` (`_require_dotnet`) — falls through to `_offer_nix_install || true`
- `visual-relay:164` (`_missing_required_tools`) — falls through to `return 0`

## Done when

- **Bug 1 fixed and tested:** `AppIconTests.AppIcon_ContainsMultipleResolutions` uses
  `WaitForExit(10_000)` at line 137; a test demonstrates that a hanging `magick`-equivalent is
  killed and fails with a clear message within ~15 s rather than blocking indefinitely. The
  existing skip-when-absent path (`AppIconTests.cs:113-120`) is untouched.
- **Bug 2 fixed and tested:** `_find_nix` returns 0 in all branches; a launcher test confirms
  that invoking `./visual-relay` with `_VISUAL_RELAY_FAKE_NO_NIX=1` (no `NIX_REENTRY`) reaches
  the install-offer or tool-missing error path instead of silently exiting 1 via `set -e`.
- `./visual-relay check` is green.
- No other behaviour changed; changed files stay under 60 lines total.
- Conventional Commit subject(s):
  - `fix(tests): bound magick WaitForExit to 10 s and kill on timeout`
  - `fix(launcher): make _find_nix always return 0 so set -e never silences the no-nix path`
