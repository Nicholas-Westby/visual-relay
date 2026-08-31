using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed class BackendConfigGeneratorProvidersTests
{
    [Fact]
    public void ProviderFor_MapsFallbackChainsSelectableAndUnknown()
    {
        Assert.Equal("Hugging Face", BackendConfigGenerator.ProviderFor("fallback"));
        Assert.Equal("DeepSeek", BackendConfigGenerator.ProviderFor("deepseek-v4-pro"));
        Assert.Equal("Moonshot", BackendConfigGenerator.ProviderFor("kimi-k2"));
        Assert.Equal("Z.AI", BackendConfigGenerator.ProviderFor("glm-5.3-flash"));
        Assert.Null(BackendConfigGenerator.ProviderFor("unknown-model"));
    }
}
