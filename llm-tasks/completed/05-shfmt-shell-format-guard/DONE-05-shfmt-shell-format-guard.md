# Enforce shfmt formatting through the shell guard, with a bootstrap carve-out

The shell-size guard counts logic lines, so its 20-line ceiling is satisfied
by layout, not size: `visual-relay` packs whole functions into ~300-470-char
`;`-chained one-liners and passes at 16 logic lines. Formatted honestly it is
68. Measured with shfmt 3.13.1 (nixpkgs) over every tracked shell script:
`visual-relay` 16→68, `.githooks/command-guard` 18→21, `.githooks/commit-msg`
20→20, `.githooks/pre-commit` 19→19, `me.sh` 4, `test.sh` 3,
`tools/dotnet-test-files.sh` 12→14. The output is idempotent and preserves
every comment. The fix: make machine formatting mandatory — a crammed script
can no longer hide lines — raise the general ceiling from 20 to 24 so honestly
formatted wrappers keep headroom (post-format `command-guard` is 21; a 20
ceiling survives only via hand-tuning, which is the disease this task cures),
and give the one genuinely irreducible script (the pre-dotnet bootstrap) a
100-line ceiling. No line-length enforcement; formatting is the anti-gaming
mechanism.

## Prescribed approach

shfmt becomes a devshell tool (nix only, never a global install). The shell
guard enforces BOTH the size limits and the formatting: `./visual-relay format`
applies `shfmt --write`; the guard surfaces (`guards` shell-size subcommand and
the `check` gate) verify with `shfmt --diff` and fail on drift. The
guard-as-test (`ShellScriptSizeGuardTests`) stays pure C# with no shfmt
dependency — exactly as C# formatting is enforced by `check`'s
`dotnet format --verify-no-changes`, not by a test. The general ceiling
becomes 24; `visual-relay` (exact root-relative path) gets a fixed
100-logic-line ceiling.

### Steps

1. flake.nix: add `shfmt` to the devshell packages list.
2. Limits, in tools/VisualRelay.Guards: raise `ShellSizeGuard.DefaultLimit`
   from 20 to 24; add `BootstrapLimit = 100` and `BootstrapPath =
   "visual-relay"` constants; `FindViolations` applies `BootstrapLimit` when a
   file's relative path equals `BootstrapPath` (ordinal), the passed limit
   otherwise, so `Violation.Limit` reports the ceiling that actually applied.
   `VISUAL_RELAY_SHELL_LINE_LIMIT` and the runner's `--max` keep overriding
   only the general limit; the bootstrap ceiling is a fixed constant with no
   knob. Update `ShellSizeGuardRunner`'s remediation line — "there is no
   allowlist" is no longer true; say the bootstrap is the single structural
   carve-out and all other logic moves to C#.
3. Shared enumeration: extract a `TrackedShellScripts` helper in
   VisualRelay.Guards (git `ls-files` through `IGitInvoker`, filter through
   `ShellScriptClassifier`, skip missing/unreadable files) and consume it from
   `ShellSizeGuardRunner`, the Cli `GuardRunner.ShellSizeAsync` gate, and the
   new format code. Three copies of that enumeration loop must not exist
   afterward.
4. Formatting check: add `ShellFormatGuard` to VisualRelay.Guards running
   `shfmt --diff -- <files>` via `ProcessCapture.RunAsync`, with NO style or
   dialect flags — a bare invocation keeps `.editorconfig` in charge (today no
   section matches shell scripts, so canonical output is shfmt defaults:
   tabs). Exit 0 → clean. Exit 1 → print the diff and
   "run ./visual-relay format". Missing binary → "shfmt not found; run through
   ./visual-relay so the nix devshell provides it" and fail — an enforcing
   guard treats a missing tool as a failure, not a pass.
5. Wire it: the `shell-size` guards subcommand and `GuardRunner.ShellSizeAsync`
   run the size check then the format check, reporting both kinds of violation
   before failing. Update the Program.cs dispatch comment, the `GuardRunner`
   and `CheckCommand` doc comments. `build` stays untouched (it runs only the
   source-enumeration guard today).
6. Apply path: after `dotnet format` succeeds, `FormatCommand` runs
   `shfmt --write -- <files>` over the same enumeration (the command goes
   async). `format` stays the single apply entry point.
7. Reformat the tree with `./visual-relay format` and commit shfmt's output
   verbatim — zero hand edits to script code. The only script text edited by
   hand is the bootstrap's own header comment, whose stated ceiling ("keep
   this file ≤20 logic lines") becomes the new 100.
8. Tests, in tests/VisualRelay.Tests: retarget the constant-pin test
   (`DefaultLimit_IsThe20LineCeiling` → asserts 24, rename to match) and pin
   `BootstrapLimit` at 100 beside it; move the at-limit synthetic in
   `OverLimitScript_IsAViolation_AtLimitScript_IsNot` from 20 to 24 (25 stays
   the over sample); extend the `FindViolations` unit tests — a synthetic
   `visual-relay` at 100 logic lines passes, at 101 violates with
   `Limit: 100`, and a nested `sub/visual-relay` still gets the general 24.
   Rewrite the `ShellScriptSizeGuardTests` doc comment: drop "no
   allowlist"/"never excused", state the bootstrap carve-out and that
   formatting is enforced by the `check` gate, not this test. Refresh the
   stale "20-line POSIX limit" comment in `WindowsLauncherSizeGuardTests`.
9. AGENTS.md: extend the Build & checks section — shell scripts are
   shfmt-formatted (apply with `./visual-relay format`, verified by `check`),
   scripts stay ≤ 24 logic lines with the bootstrap's 100-line carve-out.

### Guardrails

- No shfmt in any test. Bare `dotnet test` on the tests project outside the
  devshell must stay green; the guard-as-test remains a pure line counter.
- No global shfmt: not brew, not a checked-in binary, no PATH fallback logic
  beyond the devshell.
- No style flags and no `.editorconfig` shell section. Default shfmt output
  (tabs) is canonical, and passing printer flags would disable shfmt's native
  EditorConfig support.
- No line-length enforcement. Explicitly deferred; do not add a max-width
  check anywhere.
- Never hand-edit script code to satisfy the meter — no inlining, no
  `;`-joining, no restructuring. A wrapper that outgrows 24 moves its logic
  to C#. Comment edits are fine; layout belongs to shfmt.
- Formatting `visual-relay` while it runs is safe — the `main()` wrapper makes
  bash parse everything before any subcommand executes. Do not "fix" that.
- release.yml only publishes; it never runs `check` or tests, so CI needs no
  shfmt. Verify, do not wire it.

## Done when

Inside the devshell: `./visual-relay check` is green end-to-end; re-running
`./visual-relay format` is a no-op; hand-mangling the formatting of any one
script makes `./visual-relay guards` and `check` fail with the diff and the
remediation hint. Outside the devshell: bare `dotnet test` on the tests
project is green. All seven tracked scripts are committed shfmt-formatted,
`visual-relay` is within 100 logic lines, and every other script is within 24
with no hand-modified script code.

## Commit-message evidence

Measure at implementation time and put in the commit body (≤ 3 hyphen bullets,
≤ 20 words each, no file names or paths): per-script logic-line counts before
vs after formatting against their ceilings, and the format-then-verify no-op
proof.
