using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Hermeticity tests for <see cref="KeyEnvFile.GetEnv"/>: a supplied
/// <see cref="IEnvironmentAccessor"/> must be authoritative — it must never
/// fall through to the real process environment.
/// </summary>
public sealed class KeyEnvFileHermeticityTests
{
    private const string TestKey = "VR_TEST_HERMETICITY_KEY";

    [Fact]
    public void GetEnv_SuppliedAccessorMissingKey_ReturnsNull_WhenProcessHasKey()
    {
        // Set a key in the real process environment.
        Environment.SetEnvironmentVariable(TestKey, "process-value");
        try
        {
            var accessor = new DictionaryEnvironmentAccessor();
            // The accessor lacks the key — it must return null, NOT fall through.
            var result = KeyEnvFile.GetEnv(TestKey, accessor);
            Assert.Null(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestKey, null);
        }
    }

    [Fact]
    public void GetEnv_SuppliedAccessorHasKey_ReturnsAccessorValue()
    {
        // Set a DIFFERENT value in the real process env to prove the accessor wins.
        Environment.SetEnvironmentVariable(TestKey, "process-value");
        try
        {
            var accessor = new DictionaryEnvironmentAccessor { [TestKey] = "accessor-value" };
            var result = KeyEnvFile.GetEnv(TestKey, accessor);
            Assert.Equal("accessor-value", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestKey, null);
        }
    }

    [Fact]
    public void GetEnv_NullAccessor_ReadsProcessEnv()
    {
        Environment.SetEnvironmentVariable(TestKey, "process-value");
        try
        {
            var result = KeyEnvFile.GetEnv(TestKey, accessor: null);
            Assert.Equal("process-value", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestKey, null);
        }
    }
}
