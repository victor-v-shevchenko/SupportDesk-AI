using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Fake;

namespace SupportDesk.IntegrationTests;

/// <summary>The AI settings checks run when the real app starts (Program.cs and appsettings.json), not only in isolation.</summary>
public class AiStartupTests
{
    // Not Development, so startup doesn't touch the database
    private const string NoDatabase = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none";

    [Fact]
    public async Task The_fake_provider_stops_the_app_from_starting_in_Production()
    {
        await using var factory = new ApiFactory(NoDatabase, "Production", new Dictionary<string, string?> { ["AI:Chat:Provider"] = "Fake" });

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("AI:Chat:Provider is Fake, which isn't allowed in Production", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_api_key_stops_the_app_from_starting()
    {
        await using var factory = new ApiFactory(NoDatabase, "Production", new Dictionary<string, string?>
        {
            ["AI:Chat:Provider"] = "Anthropic",
            ["AI:Anthropic:ApiKey"] = "",
        });

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("AI:Anthropic:ApiKey is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_fake_provider_the_app_starts_and_streams_the_default_scenario()
    {
        await using var factory = new ApiFactory(NoDatabase, "Testing", new Dictionary<string, string?>
        {
            ["AI:Chat:Provider"] = "Fake",
            ["AI:Embeddings:Provider"] = "Fake",
        });
        using var client = factory.CreateClient();
        var chat = factory.Services.GetRequiredService<IChatClient>();
        var options = new AiCallContext(AiFeature.Summary, "summary.v1", TicketNumber: "T-1001").ApplyTo(new ChatOptions());

        var response = await chat.GetResponseAsync("hi", options, TestContext.Current.CancellationToken);

        Assert.Equal(FakeChatClient.ProviderName, chat.GetService<ChatClientMetadata>()?.ProviderName);
        Assert.False(string.IsNullOrWhiteSpace(response.Text));
    }
}
