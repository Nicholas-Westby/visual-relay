using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed class ModelCatalogProvidersTests
{
    [Fact]
    public void ProviderFor_MapsFallbackChainsSelectableAndUnknown()
    {
        Assert.Equal("Hugging Face", ModelCatalog.ProviderFor("fallback"));
        Assert.Equal("DeepSeek", ModelCatalog.ProviderFor("deepseek-v4-flash"));
        Assert.Equal("Moonshot", ModelCatalog.ProviderFor("kimi-k2"));
        Assert.Equal("Z.AI", ModelCatalog.ProviderFor("glm-5.3-flash"));
        Assert.Null(ModelCatalog.ProviderFor("unknown-model"));
    }
}
