using System.Reflection;

namespace VisualRelay.Tests;

/// <summary>
/// Reflection-based guard: every class with [AvaloniaFact]/[AvaloniaTheory] methods
/// must carry [Collection("Headless")] so the Avalonia process-global dispatcher is
/// never contested by parallel test classes.
/// </summary>
public sealed partial class SplitGuardVerificationTests
{
    [Fact]
    public void HeadlessTestClasses_AllCarryHeadlessCollectionAttribute()
    {
        var assembly = typeof(SplitGuardVerificationTests).Assembly;
        var violations = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition) continue;

            var hasHeadlessMethod = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Any(m =>
                    m.IsDefined(typeof(AvaloniaFactAttribute), inherit: false) ||
                    m.IsDefined(typeof(AvaloniaTheoryAttribute), inherit: false));

            if (!hasHeadlessMethod) continue;

            var collection = type.GetCustomAttribute<CollectionAttribute>();
            if (collection?.Name != "Headless")
            {
                violations.Add(
                    $"  {type.Name}: has [AvaloniaFact]/[AvaloniaTheory] but " +
                    $"[Collection] is {(collection is null ? "missing" : $"\"{collection.Name}\"")}");
            }
        }

        Assert.True(violations.Count == 0,
            "The following headless test classes are missing [Collection(\"Headless\")]:\n" +
            string.Join("\n", violations) +
            "\n\nAll classes with [AvaloniaFact] or [AvaloniaTheory] methods must declare " +
            "[Collection(\"Headless\")] so only one headless test runs at a time on the " +
            "shared Avalonia process-global dispatcher.");
    }
}
