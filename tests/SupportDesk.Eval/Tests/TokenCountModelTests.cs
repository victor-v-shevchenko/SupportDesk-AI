namespace SupportDesk.Eval.Tests;

public class TokenCountModelTests
{
    [Fact]
    public void A_non_Anthropic_provider_uses_the_default_model()
    {
        Assert.Equal(TokenCountModel.Default, TokenCountModel.Choose("Ollama", "qwen2.5:7b"));
    }

    [Fact]
    public void The_Anthropic_provider_uses_its_configured_model()
    {
        Assert.Equal("claude-haiku-4-5", TokenCountModel.Choose("anthropic", "claude-haiku-4-5"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void The_Anthropic_provider_without_a_model_uses_the_default_model(string? model)
    {
        Assert.Equal(TokenCountModel.Default, TokenCountModel.Choose("Anthropic", model));
    }
}
