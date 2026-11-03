using Microsoft.Extensions.Configuration;

namespace SupportDesk.Eval.Tests;

public class TokenCountModelTests
{
    private static string Choose(string? provider, string? model) =>
        TokenCountModel.Choose(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:Chat:Provider"] = provider,
            ["AI:Chat:Model"] = model,
        }).Build());

    [Theory]
    [InlineData("Ollama", "qwen2.5:7b")]
    [InlineData(null, "qwen2.5:7b")]
    [InlineData("OpenAI", "gpt-5")]
    [InlineData("Fake", "fake")]
    public void A_provider_other_than_Anthropic_uses_the_default_model(string? provider, string model)
    {
        Assert.Equal(TokenCountModel.Default, Choose(provider, model));
    }

    [Fact]
    public void The_Anthropic_provider_uses_its_configured_model()
    {
        Assert.Equal("claude-haiku-4-5", Choose("anthropic", "claude-haiku-4-5"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void The_Anthropic_provider_without_a_model_uses_the_default_model(string? model)
    {
        Assert.Equal(TokenCountModel.Default, Choose("Anthropic", model));
    }

    [Fact]
    public void The_default_is_the_app_default_Anthropic_model()
    {
        Assert.Equal("claude-opus-5-5", TokenCountModel.Default);
    }
}
