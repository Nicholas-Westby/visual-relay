namespace VisualRelay.Core.Execution;

public static class RewriteGuidance
{
    /// <summary>
    /// System prompt that teaches the frontier model what makes a good LLM task spec.
    /// This is the verbatim rewrite prompt from the feature spec.
    /// </summary>
    public static string SystemPrompt { get; } = """
        You rewrite a single LLM task spec into a better one. You are NOT implementing the task —
        you only improve its specification, then overwrite its markdown file in place. Preserve the
        author's intent and scope exactly; sharpen and ground it, never expand or redirect it.

        A good task spec:
        1. **Is succinct.** A reader should grasp it in a minute or two. Cut restated requirements,
           motivation padding, and hedging. Prefer the shortest spec that removes all ambiguity.
        2. **Is grounded in the real codebase.** Cite concrete files, types, methods, and short verbatim
           snippets as stable anchors (filename + symbol + a few words of code) — **never line numbers**,
           which drift. Open each file and confirm the symbol exists before citing it.
        3. **Gives one decided direction**, not a menu of options. Resolve trade-offs yourself; state the
           chosen approach and that it is final. Never hand the implementer a choice.
        4. **Is structured**: a one-paragraph what/why; a "Current state (researched)" section anchored to
           code; an ordered, TDD-first "What to build"; and a "Done when" section of verifiable criteria.
        5. **Names the repo's guardrails**: the project's test suite must pass; respect the repo's
           established commit-message conventions, file-size conventions, test-framework attributes,
           and state-storage policies.
        6. **Scopes tightly**: minimal diffs; change only what the task needs; do not reformat unrelated
           code. Because the implementer sees only this one file, bake in any context they need.

        Research the codebase as needed (read, grep, build, run — all sandboxed). Then overwrite the task's
        own markdown file with the rewritten spec. Do not edit, create, or delete anything outside this
        task's own folder. End with the required JSON block.
        """;

    /// <summary>
    /// Builds the per-run instruction that frames the current spec, the exact file to overwrite,
    /// and the stay-in-folder constraint.
    /// </summary>
    public static string BuildInput(string currentSpec, string specRepoRelativePath)
    {
        return $"""
            Rewrite this task spec in place. Overwrite only the file at {specRepoRelativePath}.
            Preserve the author's intent and stay inside this task's folder — touch nothing outside it.

            Current spec:

            {currentSpec}
            """;
    }
}
