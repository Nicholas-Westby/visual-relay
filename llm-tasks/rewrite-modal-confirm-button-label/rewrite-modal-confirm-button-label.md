# "Rewrite with AI" confirmation: the confirm button says "Delete" — should say "Rewrite and Replace"

The **Rewrite with AI** confirmation dialog ("Replace this task's spec with an AI-researched rewrite?
The current text is kept so you can revert.") has buttons **Cancel** and **Delete**. "Delete" is
wrong and alarming for a rewrite — it should read something like **"Rewrite and Replace"**. See
the screenshot.

## Cause (bake in)

The confirm button label is **hardcoded** in a shared dialog. `App.axaml.cs` →
`ShowConfirmationAsync(Window owner, string title, string message)` builds a dialog whose two buttons
are literally `Content = "Cancel"` and `Content = "Delete"`, regardless of caller. It's wired to the
VM via `viewModel.ShowConfirmationAsync = (title, message) => ShowConfirmationAsync(window, title, message);`
and the delegate type is `Func<string, string, Task<bool>>` (`MainWindowViewModel.cs`).

Both confirm call sites share this one "Delete"-labelled dialog:
- **Rewrite** — `MainWindowViewModel.Rewrite.cs`: `ShowConfirmationAsync("Rewrite with AI", "Replace
  this task's spec…")` → should confirm with **"Rewrite and Replace"**.
- **Attachment removal** — `MainWindowViewModel.Authoring.cs`: `ShowConfirmationAsync("Remove
  Attachment", "Delete \"{fileName}\"?…")` → "Delete" (or "Remove") is fine here.

> **Freshness contract.** Verify by searching for `ShowConfirmationAsync` and `Content = "Delete"`;
> adapt to the current signatures.

## Goal

The Rewrite confirmation's primary button reads **"Rewrite and Replace"**; the destructive
attachment-removal confirmation keeps an appropriate destructive label ("Delete"/"Remove"). No
dialog reuse forces a misleading label again.

## Approach

- Add a **confirm-button-label parameter** to the dialog: extend `ShowConfirmationAsync` (the
  `App.axaml.cs` method, the VM delegate type `Func<string,string,Task<bool>>` → add a 3rd `string`
  arg, and the wiring) so each caller supplies its own confirm label. A sensible default ("OK") is
  fine for callers that don't care.
- Pass **"Rewrite and Replace"** from the rewrite call site; keep **"Delete"** (or "Remove") for the
  attachment-removal call site.
- The label is longer than "Delete" — make sure the dialog's buttons size to content (current buttons
  are `Width = 80`; let the confirm button grow or widen it) so "Rewrite and Replace" isn't clipped
  in the 400-wide dialog.

## Tests

- Headless: invoke the rewrite confirmation path with a fake/captured `ShowConfirmationAsync` and
  assert it's called with the "Rewrite and Replace" confirm label (and the attachment path with its
  destructive label). If the dialog is exercised directly, assert the confirm button's `Content`.
- Keep existing rewrite/attachment confirmation tests green (the delegate signature change will
  touch their fakes).

## Out of scope

- The rewrite *failure* handling (`rewrite-opaque-failure-on-model-auth-error`).
- Restyling the dialog beyond the button label/width.

## Screenshot

- `rewrite-modal.png` — the dialog with the mislabeled "Delete" button.
