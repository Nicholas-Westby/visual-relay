using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed class BackendConfigGeneratorProvidersTests
{
    [Fact]
    public void ProviderFor_MapsFallbackChainsSelectableAndUnknown()
    {
        Assert.Equal("Hugging Face", BackendConfigGenerator.ProviderFor("fallback"));
        Assert.Equal("DeepSeek", BackendConfigGenerator.ProviderFor("deepseek-v4-pro"));
        Assert.Equal("OpenAI", BackendConfigGenerator.ProviderFor("gpt-5"));
        Assert.Null(BackendConfigGenerator.ProviderFor("unknown-model"));
    }
}
