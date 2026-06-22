# "Rewrite with AI" reports an opaque error when the model backend fails

When a "Rewrite with AI" attempt fails because the model call fails, the UI shows a misleading message
that echoes the *prompt* instead of the real cause. A real failure read:
`Rewrite of improve-live-tiers-ui failed: swival exit 1: …## Task input  Rewrite this task spec in
place.` — the actual cause was a 401 from the model backend (an invalid HuggingFace token), but you
couldn't tell that from the message; the real error lived only in the litellm proxy log. The opacity
is the bug: it sent debugging down two wrong paths before the log revealed the 401.

(The token issue itself is already resolved — this task is purely about making such failures legible
next time, independent of any specific key.)

## Why the message is useless (bake in)

- The rewrite runs swival; on a nonzero exit, `ProcessRunners.RunAsync.cs` builds
  `swival exit {code}: {ExtractFailureReason(output)}`.
- `ExtractFailureReason` (`ProcessRunners.Diagnostics.cs`) distills swival's merged stdout/stderr,
  dropping nono advisory noise and anchoring on failure markers. But when the failure is a **model
  backend error** (auth / HTTP / rate-limit), that error is in the **litellm proxy log**
  (`$XDG_DATA_HOME/visual-relay/scratch/litellm.log`), NOT in swival's own output — so no marker is
  found and it falls back to the **tail of the prompt** (`## Task input …`).
- The `(full output: <path>)` breadcrumb points into the rewrite worktree, which `TaskRewriteRunner`
  **deletes on failure** (`finally → RemoveAsync`), so even that file is gone.

> **Freshness contract.** Verify by searching for `ExtractFailureReason`, `swival exit`, and the
> `finally` / `RemoveAsync` in `TaskRewriteRunner`; adapt if they've moved.

## Goal

When a rewrite (or any stage) fails because the model backend rejected/failed the request, the user
sees the **real cause** (e.g. "model call failed — HuggingFace returned 401; check the provider key")
instead of a prompt echo, and the diagnostic breadcrumb points at a file that still exists.

## Approach (Plan/Implement to refine)

- When swival exits nonzero and `ExtractFailureReason` finds no marker in swival's own output, also
  consult the **proxy log** for a recent `AuthenticationError` / HTTP-status / `model_group=` line and
  fold it into the reason. At minimum, when swival's output yields no usable diagnostic, say so and
  point at the proxy log + likely cause (a failed model call) rather than echoing the prompt.
- Preserve the rewrite's diagnostic trace on **failure** — copy `rewrite.log` / the `exit_<n>`
  diagnostic out of the worktree before `RemoveAsync`, or write it under the task's `.relay/` — so the
  `(full output: …)` breadcrumb resolves to a file that still exists.

## Tests

- Unit (`ProcessRunners.Diagnostics` style): given swival output with **no** failure marker plus a
  proxy log containing a 401 `AuthenticationError`, the surfaced reason names the auth/model error, not
  the prompt echo.
- `TaskRewriteRunner`: after a failed rewrite, the preserved diagnostic file still exists once the
  worktree is removed.

## Out of scope

- Model routing / fallback chains (the frontier tier being all-HuggingFace was noted during this
  incident but is deliberately not part of this task).
- The rewrite-modal button label (`rewrite-modal-confirm-button-label`).
- Pre-validating provider keys.
