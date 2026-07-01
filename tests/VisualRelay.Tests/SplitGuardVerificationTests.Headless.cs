using System.Reflection;

namespace VisualRelay.Tests;

/// <summary>
/// Reflection-based guard: every class with [AvaloniaFact]/[AvaloniaTheory] methods
/// must carry [Collection("Headless")] so the Avalonia process-global dispatcher is
/// never contested by parallel test classes.
///
/// Additionally, no class carrying [Collection("Headless")] may contain a plain
/// [Fact]/[Theory] — every test method in a Headless class must be
/// [AvaloniaFact]/[AvaloniaTheory] so the Avalonia platform is always initialised
/// before any test runs. A plain [Fact] that runs first can cause a
/// TypeInitializationException in platform-dependent types (e.g. ChevronIcon)
/// that permanently poisons the type for the rest of the process.
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

    /// <summary>
    /// No class carrying [Collection("Headless")] may contain a plain
    /// [Fact]/[Theory] test method. Every test method in a Headless class must be
    /// [AvaloniaFact]/[AvaloniaTheory] so the Avalonia platform is always
    /// initialised before any test runs. A plain [Fact] that runs first can
    /// poison platform-dependent static initializers (e.g. ChevronIcon.SharedGeometry
    /// → Geometry.Parse → IPlatformRenderInterface) for the entire process.
    /// </summary>
    [Fact]
    public void HeadlessTestClasses_MustNotContainPlainFactOrTheory()
    {
        var assembly = typeof(SplitGuardVerificationTests).Assembly;
        var violations = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition) continue;

            var collection = type.GetCustomAttribute<CollectionAttribute>();
            if (collection?.Name != "Headless") continue;

            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var isFact = method.IsDefined(typeof(FactAttribute), inherit: false);
                var isTheory = method.IsDefined(typeof(TheoryAttribute), inherit: false);
                var isAvaloniaFact = method.IsDefined(typeof(AvaloniaFactAttribute), inherit: false);
                var isAvaloniaTheory = method.IsDefined(typeof(AvaloniaTheoryAttribute), inherit: false);

                // A plain [Fact] or [Theory] that is NOT also [AvaloniaFact]/[AvaloniaTheory]
                // is forbidden in a Headless class.
                if ((isFact || isTheory) && !isAvaloniaFact && !isAvaloniaTheory)
                {
                    violations.Add($"  {type.Name}.{method.Name}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "The following Headless test classes contain plain [Fact]/[Theory] methods " +
            "that do not initialise the Avalonia platform:\n" +
            string.Join("\n", violations) +
            "\n\nEvery test method in a [Collection(\"Headless\")] class must be " +
            "[AvaloniaFact]/[AvaloniaTheory] so the Avalonia platform is always " +
            "initialised. A plain [Fact] running before any [AvaloniaFact] can poison " +
            "platform-dependent static initializers (e.g. ChevronIcon.SharedGeometry) " +
            "for the entire test process, aborting the run.");
    }
}
