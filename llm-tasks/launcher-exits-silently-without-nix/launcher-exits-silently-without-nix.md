# On a fresh machine without Nix, `./visual-relay` exits silently — `set -e` kills the `nix="$(_find_nix)"` assignment before the install offer ever runs

`./visual-relay` (any subcommand, including the default `launch`) on a fresh machine with no
Nix installed prints **nothing** and returns to the prompt with exit code 1. The launcher is
supposed to detect the missing toolchain and — on a TTY — offer to install Determinate Nix
(the `DONE-bootstrap-3-offer-determinate-nix-install-with-consent` feature). That offer never
appears: the launcher dies several statements before it. The one path this breaks is the one a
brand-new user hits first.

## Root cause (verified)

`set -euo pipefail` (`visual-relay:17`) + a bare command-substitution assignment whose function
returns non-zero when Nix is absent.

- `_find_nix` (`visual-relay:31`) signals "no nix" by **empty stdout** — that is its contract
  (see the `visual-relay:30` comment, "Empty output ⇒ no nix"). But when nothing is found its
  **exit status** is non-zero: the last command it runs is
  `[[ -x /run/current-system/sw/bin/nix ]]`, which is false.
- `_ensure_devshell` (`visual-relay:36`) captures it with a **bare** assignment:
  `local nix; nix="$(_find_nix)"`. Because this is a plain assignment (not
  `local nix="$(…)"`, where `local`'s own exit code would have masked the substitution's
  status), the non-zero status propagates and `set -e` aborts the whole script **before** the
  next statement — `if [[ -z "$nix" ]]; then _offer_nix_install …`. `_find_nix` printed
  nothing, so the abort is completely silent. The same landmine sits on the second
  `nix="$(_find_nix)"` later on that line.

`bash -x ./visual-relay` ends exactly here — no `[[ -z … ]]` line is ever reached:

```
+ local nix
++ _find_nix
++ command -v nix
++ [[ -x /nix/var/nix/profiles/default/bin/nix ]]
++ [[ -x /run/current-system/sw/bin/nix ]]
+ nix=          ← script exits 1 here, silently
```

## Why the existing regression test does not catch it

A test for this exact failure already exists —
`Installer5Bootstrap3LauncherTests.NoNix_NoReentry_ReachesToolMissingNotSilentExit`
(`tests/VisualRelay.Tests/Installer5Bootstrap3LauncherTests.cs:264`), doc-commented "must reach
the install-offer / tool-missing path — not silently exit 1 via set -e because _find_nix
returned non-zero." It is **green, yet the bug ships.**

The test simulates "no nix" by exporting `_VISUAL_RELAY_FAKE_NO_NIX=1` (helper at
`…LauncherTests.cs:40` and `:105`), and the launcher's seam for that flag is
`if [[ -n "${_VISUAL_RELAY_FAKE_NO_NIX:-}" ]]; then return 0; fi` (`visual-relay:31`). That
`return 0` is the one thing a real fresh machine never does: it makes `_find_nix` exit **zero**,
so the bare assignment succeeds and `set -e` never fires. The simulation diverges from reality
at precisely the byte that matters — the exit status — so the test exercises a path the product
does not have.

(The test's stale references — variable `nix_bin`, "lines 55/67/115/164" — show it predates the
launcher's thinning into one-liners; the current single-line `_ensure_devshell` kept the
landmine while the test kept passing.)

## Fix (suggested)

Two tiny edits, **no new logic lines** — respect the ≤20-logic-line shell guard
(`visual-relay:11`, enforced by `./visual-relay guards`):

1. **Make the test seam faithful** (`visual-relay:31`): `_VISUAL_RELAY_FAKE_NO_NIX` must mimic
   real absence — empty stdout **and non-zero exit**. Change its `return 0` → `return 1`. This
   edit alone turns the existing regression test **red** (it now reproduces the real silent
   exit 1), giving you the failing test first.
2. **Tolerate the non-zero status at the call sites** (`visual-relay:36`): append `|| true` to
   **both** `nix="$(_find_nix)"` assignments. This honors `_find_nix`'s documented contract —
   branch on empty *output*, not on exit status — while leaving `set -e` in force everywhere
   else. The `if [[ -z "$nix" ]]` check then runs and reaches `_offer_nix_install`.

Equivalent alternative for (2): give `_find_nix` a total contract so it always returns 0 (e.g.
a trailing `|| true` on its body); that fixes all call sites at once. Do **not** rely on the
`local nix="$(…)"` masking trick — shellcheck flags it (SC2155) and it only covers the first of
the two sites.

## Files

- `visual-relay` — the seam flip (`:31`) and the two `|| true` call-site guards (`:36`).
- `tests/VisualRelay.Tests/Installer5Bootstrap3LauncherTests.cs` — after the seam flip the
  no-nix cases genuinely exercise the `set -e` path; confirm they pass once the launcher is
  fixed, and add the TTY-prompt assertion below.
- `tests/VisualRelay.Tests/Installer5Bootstrap2LauncherTests.cs` — also uses
  `_VISUAL_RELAY_FAKE_NO_NIX`; re-run it after the seam flip and fix any case that legitimately
  surfaces the same landmine.

## Tests (failing test first)

- With edit (1) only, `NoNix_NoReentry_ReachesToolMissingNotSilentExit` must **fail** on the
  current launcher: silent `exit 1`, no `install.determinate.systems` hint — exactly the
  production bug. Confirm red before edit (2).
- With edits (1)+(2) it must pass: non-TTY, no-nix prints the manual install hint and falls
  through to `exec dotnet run` (`visual-relay:41`), exiting 127 on a machine without dotnet.
- Add (or extend) a **TTY** no-nix case proving the launcher now **reaches the `[y/N]` prompt**
  (`grep -q '\[y/N\]'`) instead of dying — that is the user-visible symptom from the report.
  The existing decline / yes TTY tests (`…LauncherTests.cs:142`, `:180`) stay green.
- Full launcher suite (Bootstrap2 + Bootstrap3) green.

## Done when

- Fresh machine, no Nix, interactive: `./visual-relay` shows the
  `Install Determinate Nix? [y/N]` offer instead of a silent return. Non-interactive or
  declined: it prints the manual `curl … install.determinate.systems` one-liner — never a
  silent exit.
- `_VISUAL_RELAY_FAKE_NO_NIX` returns non-zero, so every no-nix launcher test now exercises the
  real `set -e` path; the regression genuinely fails without the call-site guard.
- `./visual-relay check` green (size / format / build / test / screenshot); launcher within the
  ≤20-logic-line guard; Conventional Commit subject (e.g.
  `fix(launcher): reach nix install offer instead of silent set -e exit`).

## Notes

- Defense in depth: audit every other `$(…)` assignment in `visual-relay` for the same `set -e`
  landmine. Today only `SCRIPT_DIR` (`:20`, returns 0 on success) and the two `_find_nix`
  captures exist. The pre-dotnet launcher is the worst possible place for a silent death — no
  UI, no log, no error text — so it deserves the strictest guard.
- The lesson the seam encodes: a test double for "absent" must mirror the absent path's
  **result**, exit status included. Returning success to "simulate failure" is what hid a
  shipped bug behind a green test.
