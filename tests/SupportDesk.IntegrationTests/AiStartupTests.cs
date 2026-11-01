using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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
        var failure = await StartupFailureAsync(new Dictionary<string, string?> { ["AI:Chat:Provider"] = "Fake" });

        Assert.Contains("AI:Chat:Provider is Fake, which isn't allowed in Production", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_api_key_stops_the_app_from_starting()
    {
        var failure = await StartupFailureAsync(new Dictionary<string, string?>
        {
            ["AI:Chat:Provider"] = "Anthropic",
            ["AI:Anthropic:ApiKey"] = "",
        });

        Assert.Contains("AI:Anthropic:ApiKey is required", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// Starts the app in Production with <paramref name="settings"/>, expects it to fail, and returns what it reported. Which
    /// exception reaches the test races with the failing app disposing its services inside WebApplicationFactory (the
    /// validation error, or <see cref="ObjectDisposedException"/>), so the host's "Hosting failed to start" log, written
    /// before anything is disposed, is what's checked.
    /// </summary>
    private static async Task<string> StartupFailureAsync(Dictionary<string, string?> settings)
    {
        using var logs = new LogCollector();
        await using var factory = new ApiFactory(NoDatabase, "Production", settings, logs);

        var error = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(error);
        Assert.Contains("Hosting failed to start", logs.All, StringComparison.Ordinal);
        return logs.All;
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
