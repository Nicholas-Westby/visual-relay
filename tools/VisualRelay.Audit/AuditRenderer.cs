namespace VisualRelay.Audit;

using System;
using System.Collections.Generic;
using System.Linq;

public static class AuditRenderer
{
    public sealed record Finding(
        string RuleId, string Path, int Line, string Snippet,
        string Message, string Explanation, string Direction);

    public static void Render(IReadOnlyList<Finding> findings, TimeSpan elapsed)
    {
        if (findings.Count == 0)
        {
            Console.WriteLine("Audit complete: no findings.");
            Console.WriteLine();
            Console.WriteLine("  rules: 4");
            Console.WriteLine($"  runtime: {elapsed.TotalSeconds:F1}s");
            return;
        }

        var ruleOrder = new[] { "retry-delay-loops", "di-bypass", "real-waits", "real-waits:suppression", "test-side-effects" };
        var grouped = findings
            .GroupBy(f => f.RuleId)
            .OrderBy(g => Array.IndexOf(ruleOrder, g.Key));

        foreach (var group in grouped)
        {
            Console.WriteLine($"--- {group.Key} ({group.Count()} finding(s)) ---");

            foreach (var f in group)
            {
                Console.WriteLine($"{f.Path}:{f.Line}: {f.RuleId}: {f.Message}");

                // The offending line itself, which every sibling guard runner
                // prints (see SyncOverAsyncGuardRunner). It was plumbed all the
                // way into Finding and then dropped here, so a reader had a
                // location and a rule name but never the code that tripped it.
                if (!string.IsNullOrWhiteSpace(f.Snippet))
                    Console.WriteLine($"  snippet: {f.Snippet.Trim()}");

                Console.WriteLine($"  explanation: {f.Explanation}");
                Console.WriteLine($"  direction: {f.Direction}");
                Console.WriteLine();
            }
        }

        Console.WriteLine("--- Summary ---");
        Console.WriteLine();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in findings)
        {
            counts.TryGetValue(f.RuleId, out var c);
            counts[f.RuleId] = c + 1;
        }

        Console.WriteLine($"  {"rule",-26} {"findings",8}");
        Console.WriteLine($"  {new string('-', 26)} {new string('-', 8)}");
        foreach (var ruleId in ruleOrder)
        {
            if (counts.TryGetValue(ruleId, out var count))
                Console.WriteLine($"  {ruleId,-26} {count,8}");
        }
        Console.WriteLine($"  {new string('-', 26)} {new string('-', 8)}");
        Console.WriteLine($"  {"total",-26} {findings.Count,8}");
        Console.WriteLine();
        Console.WriteLine($"  runtime: {elapsed.TotalSeconds:F1}s");
    }
}
