# Make the verify snapshot use its own node_modules inside a distro

`a7f833f2` set out to fix a verify snapshot that fails on a large ignored folder
inside a WSL distro. It half worked. The overlay half does what it was written to
do: the ignored folder becomes a real directory whose children are copied or
symlinked one at a time, the snapshot event names the arm that laid it out, and a
run the operating system refuses before any test says so in its reason. The other
half did not: verify still fails with `EACCES` on the same project, and it fails
in a way that says the snapshot's `node_modules` is not really the snapshot's.

The clue is in the stack. vite is loaded from the MAIN checkout
(`file:///home/enjay/vr-eval/i18next/node_modules/vite/...`) while the write it
then attempts lands under the snapshot
(`/home/enjay/.cache/visual-relay/wt/.../node_modules/.vite-temp/...`). So module
resolution inside the snapshot walks back out to the real checkout, and the tool
it finds there writes relative to where the snapshot says it is. The verify
sandbox denies that write. Making the directory real was necessary and was not
sufficient.

## Evidence

Windows PC, 2026-09-18, one authorised paid run against i18next
(`$0.1318` total, 45 minutes). The task itself was unrelated and its change was
correct; the run flagged on the environment.

- The failure, verbatim:

      Error: EACCES: permission denied, open
      '/home/enjay/.cache/visual-relay/wt/b59264feb4ad/35a951df3a69/1ff2f94f63bf/node_modules/.vite-temp/vitest.config.mts.timestamp-….mjs'
        at async loadConfigFromBundledFile
        (file:///home/enjay/vr-eval/i18next/node_modules/vite/dist/node/chunks/node.js:37163:3)

  The two paths are in different trees. That is the whole finding.

- **The new code path ran.** Every snapshot logged `overlay=in-distro`, four
  times, once for stage 10 attempt 1 and once for each of the three Fix-verify
  attempts. This is not a fix that failed to execute; it executed and the failure
  persisted.

- **The precondition was the one the fix was written for**, confirmed to
  reproduce on that machine before the run: `node_modules/.vite-temp` exists and
  is empty after one clean `npx vitest run`, `node_modules` at mode 755, repo
  clean at `4ebd19d`.

- **What `a7f833f2` did fix, and it matters.** The earlier round ended with a
  Fix-verify agent running `chmod 555` on the operator's real checkout. That did
  not recur: three Fix-verify attempts, all of which investigated hard, none of
  which touched the environment. `node_modules` was still 755 and owner-unchanged
  afterwards.

- **Isolation held.** `verify_isolation_lost` fired 0 times, so verify never
  silently fell back to the real checkout. `path_entry_dropped` 0.
  `ignored_paths_created` 0.

- The flag reason was the honest one: *"verify failed after 3 fix-verify attempts
  … tree unchanged across all attempts while verify stayed red; likely
  environment/harness, not the change"*. The harness correctly named itself.

## Current state

`RelayDriver.VerifySnapshot` lays out an ignored folder by copying small children
and symlinking large ones, so the folder itself is a real directory in the
snapshot but most of what it contains still points into the main checkout. For a
folder that only needs reading, that is fine and is why the rule was written that
way. For `node_modules` it is not: a tool resolved through a symlink runs with
the main checkout as its own location, and anything it writes relative to itself
either escapes the snapshot or is refused.

## Prescribed approach

**Do this before anything else, because it is free and it decides the rest.** All
three diagnoses below fit the evidence already collected, which is exactly why a
fix written from off the machine would be a coin flip dressed as an analysis. The
one datum that separates them is what `<snapshot>/node_modules` IS on disk while
a verify is running, and nobody has looked: the preserved artefacts hold the
failure but not the directory, because the snapshot was gone by the time anyone
read them. So hold a snapshot open and run `ls -la` and `readlink` inside it.
That tells you 2 from 3 immediately and costs nothing.

Then work out which of these the `EACCES` actually is, because the fix differs
and guessing between them is what produced the half-fix:

1. The write target inside the snapshot exists but is not in the sandbox's
   writable set, in which case the grant is what to widen.
2. The write target is itself a symlink into the main checkout, in which case the
   per-child rule needs `.vite-temp`-shaped entries (present, empty, written to
   at run time) copied rather than linked, whatever their size.
3. Module resolution needs the snapshot's own `node_modules` to be real enough
   that a tool loaded from it reports the snapshot as its location, in which case
   the copy/link split is the wrong shape for this folder and it needs a bind
   mount or a full copy.

Prefer a general mechanism over a `node_modules` special case, in keeping with
the project's principles. If the honest answer is that this folder needs
different treatment from every other ignored folder, say so in the code and name
the property that makes it different, rather than the tool or the ecosystem.

## Tests

- A snapshot fact that a child which exists, is empty and is written to during a
  run ends up writable inside the snapshot rather than linked out of it.
- A fact that a tool resolved from the snapshot's ignored folder reports a path
  inside the snapshot, however that ends up being achieved.
- Keep the existing facts green: the in-distro arm is named in the event, the
  refusal path still reports the operating system's own reason, and
  `verify_isolation_lost` still fires when isolation really is lost.

## Verification

This cannot be verified on macOS. It needs a real WSL distro, the i18next
precondition above, and one run. The previous run's artefacts are preserved at
`.relay/name-the-hasloadednamespace-guard-warnings-consistently/` on that machine
and are not to be cleaned until this task is done: 43 files, including a 566 KB
run.log carrying all three Fix-verify attempts' reasoning, status.json, the
flagged bundle `329d081b`, per-stage input/report JSON, and four verify-output
files. Two of those attempts reached the overlayfs copy-up conclusion
independently, so read them before spending another run. What they do NOT contain
is the snapshot directory itself, which is the first thing to capture.

## Out of scope

- The unrelated task the flagged run was carrying. Its change was correct.
- The out-of-plan edits that run's Fix-verify made. That gap is fixed separately.

## Rejected alternatives

- **Copy the whole ignored folder.** It was rejected when the overlay was written
  and the reason has not changed: on a real project this is gigabytes per
  snapshot. It is listed under the prescribed approach only as the fallback if
  the first two diagnoses are both wrong.
- **Pin the tool's temp directory somewhere writable.** That is per-tool
  knowledge, which this project prefers not to accumulate, and it would fix vite
  while leaving the next tool with the same problem.
