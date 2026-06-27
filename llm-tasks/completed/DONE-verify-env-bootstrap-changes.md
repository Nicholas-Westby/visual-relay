# Green runs can commit changes that brick the dev-environment bootstrap

The Verify stage runs the test suite **inside the already-provisioned environment**, so
a change that breaks environment *provisioning* sails through green and detonates on the
next fresh entry. Observed (2026-06-09): sandbox-2's run added `nono` to the
`flake.nix` dev shell; every stage passed (tests executed in the existing nix shell) and
`eaef5be` committed. The very next `nix develop` entry — the drive's invocation of the
following task — failed hard: nixpkgs builds nono 0.53.0 from source and its check phase
fails inside the nix build sandbox ("Refusing to grant '/nix' … overlaps protected nono
state root"). The whole pipeline was bricked for every subsequent invocation until a
human reverted the line (`734a551`). Verify validated the code, not the bootstrap the
code changed.

This is general: flake.nix/flake.lock, Brewfile, Dockerfile, .tool-versions,
package.json engines/packageManager, rust-toolchain.toml — any file consumed by "enter
the dev environment" rather than by the build/tests themselves has this blind spot.

## Goal

When a run modifies environment-bootstrap files, the pipeline proves the bootstrap still
works **from a fresh evaluation** before committing — a red bootstrap check is treated
like a red test run (enters the fix-verify loop, never silently commits). Runs that
don't touch bootstrap files pay zero extra cost.

## Approach (suggested)

- Detect bootstrap changes from the run's manifest: a default glob set (`flake.nix`,
  `flake.lock`, `*.nix`, `Brewfile`, `Dockerfile*`, `.tool-versions`,
  `rust-toolchain*`), overridable/extendable via `.relay/config.json` (e.g.
  `bootstrapFiles` + `bootstrapCheckCmd`).
- When matched, run the smoke command at the stage-9 gate alongside the test run, e.g.
  default for a flake repo: `nix develop --command true` (cheap when cached, and a fresh
  evaluation is exactly what re-builds a changed shell). Feed failures into the existing
  fix-verify loop the same way failing tests are fed (`RelayDriver.cs:181-220`); time-box
  it like the test runner (the cap-and-degrade machinery) so a huge rebuild can't hang
  the stage.
- Config-driven so it works for any repo/toolchain (a bun repo might use
  `bun install --frozen-lockfile --dry-run`; the default only fires on the built-in
  globs it recognizes).
- Tests at the driver level (mirroring the TestRunner fakes): (a) manifest touching
  `flake.nix` triggers the bootstrap check; (b) untouched → no check; (c) red bootstrap
  check enters fix-verify with the check's output; (d) configured override command and
  globs are honored.
