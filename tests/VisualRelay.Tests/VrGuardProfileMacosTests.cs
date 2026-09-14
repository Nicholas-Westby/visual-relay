using System.Text.Json;

namespace VisualRelay.Tests;

/// <summary>
/// The seatbelt rules vr-guard adds on macOS. Measured with openai/openai-agents-python under
/// nono with this profile: the six tests that take a Python multiprocessing lock failed at
/// <c>multiprocessing.SemLock</c> with "PermissionError: [Errno 1] Operation not permitted",
/// because the profile allowed POSIX shared memory but not POSIX semaphores.
/// </summary>
public sealed class VrGuardProfileMacosTests
{
    [Theory]
    [InlineData("(allow ipc-posix-shm*)")]
    [InlineData("(allow ipc-posix-sem*)")]
    public void SeatbeltRules_AllowThePosixIpcThatProcessPoolsUse(string rule)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoSetup.Root, "packaging", "nono", "vr-guard.json")));

        var rules = doc.RootElement.GetProperty("unsafe_macos_seatbelt_rules").EnumerateArray().Select(r => r.GetString());

        Assert.Contains(rule, rules);
    }
}
