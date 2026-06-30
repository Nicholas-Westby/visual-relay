## Stage 1 - Ideate

{
  "summary": "Display image attachments inline in the Avalonia UI — detect image files, render them at 50–75% panel width with a click-to-open handler, while preserving the existing 'Reveal' and 'Remove' buttons.",
  "options": [
    "Option A: Add an `Image` element and a file-extension `IValueConverter` inside the existing attachment DataTemplate in XAML. The converter returns an `Image.Source` for known image extensions (or null otherwise). Bind `Image.IsVisible` to the converter output, and add a `PointerPressed` handler that calls `TopLevel.Launcher.LaunchFileInfoAsync()` with the attachment path. Size the `Image` via `MaxWidth=\"*\"` bound to a percentage of the parent panel's `ActualWidth` (e.g. 0.75). This is the lowest-touch approach — no new files, no new view models.",
    "Option B: Create a new `ImageAttachmentView` UserControl (XAML + code-behind) with its own view model or attached properties for file path, preview sizing, and click-to-open. Add a `DataTemplateSelector` on the parent `ItemsControl` that picks this view for image extensions and the default view for everything else. This keeps image-specific layout logic isolated and testable.",
    "Option C: Extend the attachment ViewModel with an `IsImage` flag and a `PreviewImage` property (set by inspecting the file path / MIME type). The ViewModel exposes an `OpenImageCommand` that wraps `Launcher.LaunchFileInfoAsync()`. The XAML DataTemplate then binds to these properties directly, keeping the view declarative. This moves detection logic into the model layer but keeps the XAML minimal."
  ]
}

## Stage 2 - Research

{ "findings": "The attachment UI lives in TaskDetailPanel.axaml (Attachments tab) using an ItemsControl with a DataTemplate over AttachmentRowViewModel (Path, RevealCommand, RemoveCommand). Attachments are populated from task SiblingPaths (full filesystem paths). No image preview, image detection, or click-to-open functionality exists today. Avalonia 12.0.5 provides Bitmap (from Avalonia.Media.Imaging) for file loading and TopLevel.Launcher.LaunchFileInfoAsync() for OS-default file opening. Converters follow a singleton-Instance pattern in Views/Controls/. Compiled bindings are enabled project-wide. No DataTemplateSelector or Image elements exist in the codebase currently.", "constraints": ["Preserve existing Reveal and Remove buttons for image attachments", "Image sizing must be 50–75% of available panel width (use ~0.75 * parent ActualWidth via converter or MaxWidth)", "Clicking the image must open it in the default OS viewer via TopLevel.Launcher.LaunchFileInfoAsync()", "Compiled bindings (x:DataType) are active in the DataTemplate — new bindings must work with them", "Minimal file changes preferred (Option A: one new converter + XAML/code-behind edits)", "No existing async image loading — synchronous Bitmap(filePath) is acceptable for initial implementation", "Supported image extensions: .png, .jpg, .jpeg, .gif, .bmp, .webp", "Dark theme background (#12151B) must be considered for image rendering"] }

## Stage 3 - Diagnose

{
  "evidence": "The test suite is broken due to a nono sandbox profile permission error. The root cause is `NonoProfileEnsurer.EnsureAsync()` (src/VisualRelay.Core/Execution/NonoProfileEnsurer.cs:94-105) failing with `System.UnauthorizedAccessException: Access to the path '/Users/nicholaswestby/.config/visual-relay/vr-guard.json' is denied.` — caused by `System.IO.IOException: Operation not permitted`. This kernel-level denial (macOS Seatbelt) prevents writing the sandbox profile, which in turn makes the sandbox profile missing or stale. The cascade: every integration test that calls `TaskRewriteRunner.RunAsync()` or `RelayDriver.RunTaskAsync()` hits this failure at stage startup, causing the task outcome to flip from expected 'Committed'/'Planned' to 'Flagged' with the profile-write error message. Additionally, the Swival agent itself experiences sandbox denials (`Path 'BASEDIR' resolves to BASEDIR, which is outside .swival/ (filesystem access is disabled)`), a task context screenshot is missing from the expected path, a UI diagnostic shows `ItemsPanelRoot is null: True` for the attachments list, and there is a nix coreutils incompatibility (`invalid option -- 'm'`).",
  "excerpts": [
    "[xUnit.net 00:00:01.18] VisualRelay.Tests.RelayDriverEarlyImplementationTests.Implement_StaysBalanced_WhenNoFrontLoad [FAIL]\n  Error Message: Assert.Equal() Failure: Values differ\nExpected: Committed\nActual:   Flagged\n  Stack Trace: at VisualRelay.Tests.RelayDriverEarlyImplementationTests.Implement_StaysBalanced_WhenNoFrontLoad() in .../RelayDriverEarlyImplementationTests.cs:line 65",
    "System.InvalidOperationException : Failed to write the vr-guard sandbox profile to '/Users/nicholaswestby/.config/visual-relay/vr-guard.json'. VR will not run a sandboxed stage with a missing or stale profile. Check filesystem permissions on that path. (Access to the path '/Users/nicholaswestby/.config/visual-relay/vr-guard.json' is denied.)\n---- System.UnauthorizedAccessException : Access to the path '/Users/nicholaswestby/.config/visual-relay/vr-guard.json' is denied.\n-------- System.IO.IOException : Operation not permitted\n   at VisualRelay.Core.Execution.NonoProfileEnsurer.EnsureAsync(...) in .../NonoProfileEnsurer.cs:line 105\n   at VisualRelay.Core.Execution.TaskRewriteRunner.RunAsync(...) in .../TaskRewriteRunner.cs:line 53",
    "error: Path 'BASEDIR' resolves to BASEDIR, which is outside .swival/ (filesystem access is disabled) [run.log lines 9-10]",
    "error: file not found: BASEDIR/.relay/display-images-inline-for-attachments/Screenshot 2026-06-30 at 9.30.34 AM.png [run.log line 140]",
    ".swival/diag.txt: traceList is null: True / ItemsPanelRoot is null: True / ItemsPanelRoot type: N/A",
    "/nix/store/y0ha8v4gh5vnwwmp2r0msfbdlvgwv2np-coreutils-9.11/bin/coreutils: invalid option -- 'm' [bg_a27fbb7eaa14.log]",
    "CSSM_ModuleLoad(): One or more parameters passed to a function were not valid. [bg build output, repeated across builds]"
  ],
  "repro": "Run `dotnet test tests/VisualRelay.Tests/` on macOS under the nono sandbox. The NonoProfileEnsurer attempts to write to ~/.config/visual-relay/vr-guard.json but the sandbox profile's Seatbelt rules block that write with EPERM/Operation not permitted. The test assertions expect Committed/Planned outcomes but get Flagged instead because the task runner cannot initialize the sandboxed subagent. Also reproducible: the Swival agent cannot access paths outside .swival/ due to its own sandbox profile restrictions."
}

## Stage 4 - Plan

{
  "plan": "## Summary\nAdd inline image preview to the Attachments tab by extending the existing DataTemplate in TaskDetailPanel.axaml with three new singleton converters (ImagePathToBitmapConverter, IsImageExtensionConverter, FractionConverter) and a PointerPressed code-behind handler. Non-image attachments render identically to before; image attachments gain a large preview above the existing Reveal/Remove buttons and path text. No ViewModel changes needed.\n\n## Step-by-step\n\n### 1. Create ImagePathToBitmapConverter\n**New file:** `+src/VisualRelay.App/Views/Controls/ImagePathToBitmapConverter.cs`\n\nSingleton IValueConverter following the existing HomePathToTildeConverter / BoolNotConverter pattern. Convert() takes a string? path, checks the file extension (case-insensitive) against `.png`, `.jpg`, `.jpeg`, `.gif`, `.bmp`, `.webp`, and loads `new Bitmap(path)` synchronously. Returns `Bitmap?` — null for non-image extensions or missing files. ConvertBack() returns the value unchanged.\n\n### 2. Create IsImageExtensionConverter\n**New file:** `+src/VisualRelay.App/Views/Controls/IsImageExtensionConverter.cs`\n\nSingleton IValueConverter. Convert() returns true if the path string ends with a known image extension (same set as above). Drives Image.IsVisible so the Image element collapses for non-image attachments.\n\n### 3. Create FractionConverter\n**New file:** `+src/VisualRelay.App/Views/Controls/FractionConverter.cs`\n\nSingleton IValueConverter. Convert() takes a double? and multiplies it by ConverterParameter parsed as double (default 0.75). Used to compute Image.MaxWidth from the parent ScrollViewer's Viewport.Width. ConvertBack() is a pass-through.\n\n### 4. Modify TaskDetailPanel.axaml — add Image element\n**Edit file:** `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml`\n\n- Add `x:Name=\"AttachmentsScroller\"` to the ScrollViewer on line 225.\n- Replace the DataTemplate's single child (Border wrapping a DockPanel) with a Border wrapping a StackPanel:\n  - **New Image element** (first child):\n    - Source bound to Path with ImagePathToBitmapConverter\n    - IsVisible bound to Path with IsImageExtensionConverter\n    - MaxWidth bound to `#AttachmentsScroller.Viewport.Width` with FractionConverter(0.75)\n    - Stretch=\"Uniform\", StretchDirection=\"DownOnly\"\n    - Margin=\"0,0,0,6\"\n    - PointerPressed=\"OnAttachmentImagePointerPressed\"\n  - **Existing DockPanel** (second child, unchanged) with Remove/Reveal buttons and path TextBlock.\n\n### 5. Modify TaskDetailPanel.axaml.cs — add click handler\n**Edit file:** `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml.cs`\n\nAdd `OnAttachmentImagePointerPressed(object? sender, PointerPressedEventArgs e)` method. Casts sender to Image, gets DataContext as AttachmentRowViewModel, extracts Path, calls `TopLevel.GetTopLevel((Visual)sender)?.Launcher?.LaunchFileInfoAsync(new FileInfo(path))`. Null-safe throughout.\n\n### 6. Create converter unit tests\n**New file:** `+tests/VisualRelay.Tests/ImagePathToBitmapConverterTests.cs`\n\nxUnit Fact tests (no Avalonia UI needed — just converter logic):\n- Known image extensions (.png, .jpg, .jpeg, .gif, .bmp, .webp) return non-null Bitmap when file exists\n- Non-image extensions (.txt, .md, .pdf) return null\n- Null/empty path returns null\n- Missing file returns null\n- Instance is singleton\n- ConvertBack pass-through\n- IsImageExtensionConverter returns true for image extensions, false otherwise\n- FractionConverter multiplies correctly with parameter, defaults to 0.75\n\n### 7. Create UI integration tests\n**New file:** `+tests/VisualRelay.Tests/AttachmentImageDisplayTests.cs`\n\n[Collection(\"Headless\")] + [AvaloniaFact] tests following the pattern in TaskDetailAttachmentRevealButtonLayoutTests.cs:\n- Image attachment renders an Image element in the visual tree with non-null Source\n- Non-image attachment does NOT render any Image element in the item template\n- Both Reveal and Remove buttons are present for image attachments (structural check)\n- Image element has PointerPressed event wired (verify via visual tree inspection)\n- Image MaxWidth property constraint is set (structural check on the Image control's MaxWidth)",
  "manifest": [
    "+src/VisualRelay.App/Views/Controls/ImagePathToBitmapConverter.cs",
    "+src/VisualRelay.App/Views/Controls/IsImageExtensionConverter.cs",
    "+src/VisualRelay.App/Views/Controls/FractionConverter.cs",
    "src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml",
    "src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml.cs",
    "+tests/VisualRelay.Tests/ImagePathToBitmapConverterTests.cs",
    "+tests/VisualRelay.Tests/AttachmentImageDisplayTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/ImagePathToBitmapConverterTests.cs",
    "tests/VisualRelay.Tests/AttachmentImageDisplayTests.cs"
  ],
  "rationale": "Created two test files covering the three new converters and the UI integration. ImagePathToBitmapConverterTests.cs contains 30 tests: 16 for ImagePathToBitmapConverter (6 image-extension bitmap loads, 3 non-image-extension null returns, null/empty/missing-path null returns, singleton, ConvertBack pass-through with bitmap and null), 7 for IsImageExtensionConverter (12-extension Theory, null-path, singleton, ConvertBack pass-through and null), and 7 for FractionConverter (5-parameter Theory, default-0.75, null/non-double returns null, non-numeric parameter fallback, singleton, ConvertBack pass-through and null). AttachmentImageDisplayTests.cs contains 6 headless Avalonia integration tests: image renders Image element with non-null Source, non-image renders no Image, Reveal+Remove buttons present for images, Image has finite MaxWidth, Image has Uniform Stretch, Image has DownOnly StretchDirection. Build fails with CS0103 for all three converter names (expected — classes don't exist yet)."
}

## Stage 6 - Implement

{
  "summary": "Implemented inline image display for attachments with 55/55 tests passing. Created three singleton converters (ImagePathToBitmapConverter, IsImageExtensionConverter, FractionConverter) following the existing HomePathToTildeConverter pattern. Modified TaskDetailPanel.axaml to add an Image element above the existing DockPanel in the attachment DataTemplate, with bindings for Source, IsVisible, and MaxWidth (75% of ScrollViewer viewport). Added an x:Name=\"AttachmentsScroller\" to the ScrollViewer for the MaxWidth binding reference. Modified TaskDetailPanel.axaml.cs to add an OnAttachmentImagePointerPressed handler that opens the image via TopLevel.Launcher.LaunchUriAsync. Fixed one test (NonImageAttachment_DoesNotRenderImageElement) to filter for IsVisible since the Image element always exists in the visual tree but collapses for non-image types."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Verified: all 55 tests pass (0 failures). Three converters (ImagePathToBitmapConverter, IsImageExtensionConverter, FractionConverter), the XAML Image element with bindings, and the PointerPressed handler in TaskDetailPanel.axaml.cs are all in place and working correctly." }

## Stage 9 - Verify

{
  "summary": "Inline image preview for attachments: 3 singleton converters (ImagePathToBitmapConverter, IsImageExtensionConverter, FractionConverter) added under Views/Controls/. TaskDetailPanel.axaml gains an Image element above the existing DockPanel in the DataTemplate, bound at 75% of ScrollViewer viewport width, with PointerPressed→LaunchUriAsync in code-behind. All 2423 functional tests pass; 3 SplitGuard file-size violations are the sole failures (test files exceed 300-line limit at 378 and 355 lines).",
  "commitMessages": [
    "feat: display image attachments inline with click-to-open",
    "feat(attachments): add image preview with converters and PointerPressed handler",
    "feat: show inline image preview for .png/.jpg/.gif/.bmp/.webp attachments",
    "feat(ui): render attachment images at 75% panel width with Reveal/Remove buttons preserved",
    "feat: add ImagePathToBitmap, IsImageExtension, and Fraction converters for inline attachment preview"
  ]
}

## Stage 10 - Fix-verify (attempt 1/3)

{
  "summary": "Fixed all 3 gate failures from the verify stage:\n\n1. **TaskDetailRemoveButtonLayoutTests.ItemTemplateBorder_PaddingRight_IsTenOrMore** — The XAML DataTemplate structure changed from Border→DockPanel to Border→StackPanel→DockPanel, breaking the visual-tree walk. Fixed by replacing the StackPanel wrapper with a single DockPanel (Image docked Top), preserving the original Border→DockPanel parent relationship while keeping the image above the button row.\n\n2. **SplitGuardVerificationTests.AllTestCsFiles_AreAtMost300Lines / FileSizeGuard_ReportsNoViolations** — Both test files exceeded the 300-line limit:\n   - `ImagePathToBitmapConverterTests.cs` (378 lines) → split into 3 files: `ImagePathToBitmapConverterTests.cs` (225 lines), `IsImageExtensionConverterTests.cs` (73 lines), `FractionConverterTests.cs` (85 lines)\n   - `AttachmentImageDisplayTests.cs` (355 lines) → split into 2 files: `AttachmentImageDisplayTests.cs` (191 lines, 3 behavioral tests), `AttachmentImageDisplayPropertiesTests.cs` (189 lines, 3 property/styling tests)\n\nResult: 0 failed, 2426 passed, 22 skipped.",
  "amendManifest": [
    "+tests/VisualRelay.Tests/IsImageExtensionConverterTests.cs",
    "+tests/VisualRelay.Tests/FractionConverterTests.cs",
    "+tests/VisualRelay.Tests/AttachmentImageDisplayPropertiesTests.cs"
  ]
}

## Stage 11 - Commit

Committed by Visual Relay.

