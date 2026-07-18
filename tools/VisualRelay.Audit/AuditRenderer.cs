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
            Console.WriteLine(string.Format("  runtime: {0:F1}s", elapsed.TotalSeconds));
            return;
        }

        var ruleOrder = new[] { "retry-delay-loops", "di-bypass", "real-waits", "real-waits:suppression", "test-side-effects" };
        var grouped = findings
            .GroupBy(f => f.RuleId)
            .OrderBy(g => Array.IndexOf(ruleOrder, g.Key));

        foreach (var group in grouped)
        {
            Console.WriteLine(string.Format("--- {0} ({1} finding(s)) ---", group.Key, group.Count()));

            foreach (var f in group)
            {
                Console.WriteLine(string.Format("{0}:{1}: {2}: {3}", f.Path, f.Line, f.RuleId, f.Message));
                Console.WriteLine(string.Format("  explanation: {0}", f.Explanation));
                Console.WriteLine(string.Format("  direction: {0}", f.Direction));
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

        Console.WriteLine(string.Format("  {0,-26} {1,8}", "rule", "findings"));
        Console.WriteLine(string.Format("  {0} {1}", new string('-', 26), new string('-', 8)));
        foreach (var ruleId in ruleOrder)
        {
            if (counts.TryGetValue(ruleId, out var count))
                Console.WriteLine(string.Format("  {0,-26} {1,8}", ruleId, count));
        }
        Console.WriteLine(string.Format("  {0} {1}", new string('-', 26), new string('-', 8)));
        Console.WriteLine(string.Format("  {0,-26} {1,8}", "total", findings.Count));
        Console.WriteLine();
        Console.WriteLine(string.Format("  runtime: {0:F1}s", elapsed.TotalSeconds));
    }
}
