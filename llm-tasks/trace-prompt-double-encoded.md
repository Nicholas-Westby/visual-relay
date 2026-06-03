# Fix double-encoded stage prompts in LLM COMMANDS / traces

The LLM COMMANDS panel (and saved reports/traces) render escape sequences as literal
text instead of real characters — e.g. `# Relay stage 1: Ideate\nTask: add-multiply\n\n##
Task input\n...`multiply(left: int, right: int) -> int`...` instead of real
newlines, backticks, and `>`.

Root cause is at the source, not the renderer. In
`src/VisualRelay.Core/Execution/ProcessRunners.cs:42` the swival command line is built as:

```csharp
ProcessCapture.RunAsync(_swivalBinary, $"{args} {JsonSerializer.Serialize(prompt)}", ...)
```

`JsonSerializer.Serialize(prompt)` turns the prompt into a JSON string literal — backtick
becomes ```, `>` becomes `>`, newlines become `\n` — and that escaped text is
passed to swival as a single command-string argument. Swival never JSON-decodes it, so the
model and the echoed `task` field receive the literal escapes. You can see the corrupted
text in any `.relay/<task>/stage*-attempt*.report.json` `task` field (note the doubled
`\\n`, `\\u0060`). `RelayTraceParser` then faithfully displays it.

## Recommended fix

Pass swival's arguments through the existing `ArgumentList`-based overload
`ProcessCapture.RunAsync(string fileName, IEnumerable<string> arguments, ...)` (already in
the same file, lines ~206-220), supplying the prompt as a raw, unescaped argument. Build
the argument list from `BuildArguments`/`BuildPrompt` as `List<string>` values (drop the
hand-rolled `Quote` join and the `JsonSerializer.Serialize(prompt)` call). `ArgumentList`
escapes each argument correctly for the OS, so swival receives raw markdown with real
backticks and newlines — fixing reports, traces, and the LLM COMMANDS panel in one place.
Do **not** add a display-time unescape in `RelayTraceParser`; fix the producer so artifacts
are clean.

## Done when

- A freshly generated `stage*-attempt*.report.json` `task` field contains real newlines and
  backticks (no `\n`, ```, or `>` literals).
- A unit test on the runner/argument builder asserts the prompt argument is passed raw
  (contains a literal backtick, not ```). Write the failing test first.
- `./visual-relay check` is green; source files stay under 300 lines; Conventional Commit.
