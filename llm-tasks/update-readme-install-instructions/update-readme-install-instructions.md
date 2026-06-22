# README: make "clone + `./visual-relay`" the primary install path; demote unpublished brew

> Original ask: *"Most people will just clone the repo and then start running Visual Relay by calling
> ./visual-relay. This should be reflected in the README (which I think mentions brew or something that
> isn't fully supported yet)."*

The README leads with a Homebrew install that **does not actually work yet**, while the real
get-started path (clone the repo, run `./visual-relay`) is a secondary section. Flip that.

## Current state (researched — verify before editing)

- **README leads with brew:** `README.md` (~line 9) `brew install nicholas-westby/tap/visual-relay`,
  with prose framing brew/curl as the recommended install. The **source-checkout** path
  (`./visual-relay launch`) is a later, secondary section (~line 30).
- **Brew isn't publishable yet:** `packaging/visual-relay.rb` exists but its release URLs point at a
  `v0.1.0` GitHub release and the `sha256` values are literal placeholders
  (`REPLACE_WITH_ACTUAL_SHA256_ARM64` / `_X64`). So `brew install …` would fail for a real user today.
  The formula isn't published to the tap.
- **`./visual-relay` bootstrap reality** (the `visual-relay` wrapper): a source checkout runs through
  a **nix devshell** that provisions `dotnet`, `nono`, `uv`, `python3.13`. If `nix` is absent, on an
  interactive terminal it offers a single `[y/N]` to install Determinate Nix, else prints a manual
  one-liner. So the honest prereqs for "clone + run" are: a clone, then `./visual-relay` (which
  handles the rest via nix, with consent).

> **Freshness contract.** Re-read the README install section and the `visual-relay` wrapper before
> editing; quote current lines rather than these summaries if they've drifted.

## Goal

A new reader's first, recommended path is: **clone the repo → `./visual-relay`** (with an accurate
description of the nix/Determinate-Nix bootstrap and the `uv`/`nono` prereqs). Homebrew is either
removed or clearly marked **"not yet available — coming once a release is published"**, so nobody is
told to run a `brew install` that 404s.

## Approach

- Restructure the README install/getting-started section: lead with **clone + `./visual-relay launch`**,
  describing the real prerequisites (git; then `./visual-relay` enters the nix devshell, offering
  Determinate Nix if needed; `uv` for the LiteLLM backend, `nono` for sandboxing — both provided by
  the devshell).
- Demote Homebrew to a clearly-flagged "future / once released" note (or remove it until the release +
  formula SHAs are real). Don't present a non-working `brew install` as the primary path.
- Keep accurate existing details (no `.app` double-click; terminal app; quarantine note only where it
  still applies).

## Tests

- `tests/VisualRelay.Tests/Installer5DocsTests.cs` asserts README/install-doc content — update its
  expectations to match the new structure (search it for the brew/install assertions).
- Keep other docs tests green.

## Out of scope

- Actually publishing the Homebrew release / filling in the formula SHAs (separate release task).
- Changing the `visual-relay` wrapper behavior.
