# Update README with Sandbox Note

Add a single-sentence warning inside the README's Windows install section noting that the Windows
sandbox (MXC) is not yet as robust as macOS's `nono`, pointing to TROUBLESHOOTING.md for the
detail. This is a one-line prose edit; no code or tests change.

## Current state (researched)

- `README.md` line 10 already names both sandboxes: "All LLM interactions are sandboxed
  ([nono](https://nono.sh/) on macOS and [mxc](https://github.com/microsoft/mxc) on Windows) to
  avoid destructive file system changes." No per-section caveat exists today.
- The Windows install section is `# Install (Windows)` through the `<!-- END install section -->`
  marker. It opens inside the macOS section's self-contained block (`<!-- BEGIN install section
  (self-contained; sibling tasks may shorten the README) -->`), so the note must live *inside* that
  block to survive sibling-task shortening. The macOS section itself has no such warning and must
  stay untouched.
- The real MXC limitation already documented in `TROUBLESHOOTING.md` (the "Task execution is
  blocked" entry): Windows confines writes via Microsoft Execution Containers (MXC); when
  `wxc-exec` is not provisioned and no opt-in is set, execution is blocked rather than run
  uncontained, and where the BaseContainer/processcontainer backend is unavailable MXC falls back
  to the weaker AppContainer + DACL tier (needs `wxc-host-prep prepare-system-drive`). This is the
  "not very robust yet" grounding.
- No test asserts README install-section text (the `provision-mxc` test matches are command-
  dispatch in `RunAllModesTests*`, unrelated). The only README-aware check is the screenshot render
  run by `./visual-relay check`. `.editorconfig` sets only `end_of_line=lf` and
  `insert_final_newline=true` for `*.md` (no line-length rule); match the surrounding paragraph
  wrap (~96–100 cols, as in the `# Install (macOS)` prose) for consistency.

## What to build

1. In `README.md`, insert exactly one sentence inside the Windows install section, immediately
   after the closing prose line ("You can then run `visual-relay` in that folder the next time you
   want to launch it.") and before the `<!-- END install section -->` marker. Use this wording
   (final — do not paraphrase or expand):

   > Note: the Windows sandbox (MXC) is not yet as robust as macOS's `nono` due to current MXC
   > limitations; see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

2. Wrap the sentence to match the surrounding paragraph column width. Add a blank line before it so
   it reads as its own paragraph.
3. Change nothing else: leave the macOS section, line 10's sandbox sentence, the install code
   blocks, and all other README content byte-for-byte as-is.

## Done when

- The Windows install section contains exactly one new sentence matching the wording above,
  located inside the `BEGIN`/`END install section` block.
- The sentence links to `TROUBLESHOOTING.md` and names MXC as the cause of the limitation.
- The macOS section and line 10 are unchanged.
- `./visual-relay check` still passes (notably the README screenshot render); the file ends with a
  newline and uses LF line endings.
