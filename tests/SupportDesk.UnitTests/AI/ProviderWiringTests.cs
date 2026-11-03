using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Api.AI;
using SupportDesk.Core.AI;

namespace SupportDesk.UnitTests.AI;

/// <summary>
/// The clients the composition root builds (PLAN.md 6.2), not clients the tests build themselves: the SDKs don't retry,
/// the <see cref="HttpClient"/> has no overall timeout, and the connect timeout comes from the settings.
/// </summary>
public class ProviderWiringTests
{
    private const string Key = "sk-test-0123456789";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Anthropic", 529)]
    [InlineData("OpenAI", 503)]
    public async Task The_clients_the_root_builds_send_one_request_on_a_transient_error(string provider, int status)
    {
        using var handler = new StubHttpHandler((HttpStatusCode)status, """{"error":{"message":"busy","type":"overloaded_error"}}""");
        var hooks = new RecordingHooks(handler);
        using var services = Build(provider, hooks);

        var error = await Assert.ThrowsAsync<ModelUnavailableException>(() =>
            services.GetRequiredService<IChatClient>().GetResponseAsync("hi", new AiCallContext(AiFeature.Reply, "reply.v1", 1, "T-1001").ApplyTo(new ChatOptions()), Ct));

        Assert.Equal(status, error.StatusCode);
        // The pipeline is set to one attempt, so a second request could only be the SDK retrying by itself
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    [InlineData("Ollama")]
    public void Every_client_has_an_infinite_HttpClient_timeout_and_the_configured_connect_timeout(string provider)
    {
        using var handler = new StubHttpHandler(HttpStatusCode.OK, "{}");
        var hooks = new RecordingHooks(handler);
        using var services = Build(provider, hooks);

        _ = services.GetRequiredService<IChatClient>();

        var (client, real) = Assert.Single(hooks.Created);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(7), real.ConnectTimeout);
    }

    [Fact]
    public void The_Voyage_embedding_client_has_the_same_timeouts()
    {
        using var handler = new StubHttpHandler(HttpStatusCode.OK, "{}");
        var hooks = new RecordingHooks(handler);
        using var services = Build("Anthropic", hooks, new Dictionary<string, string?> { ["AI:Embeddings:Provider"] = "Voyage", ["AI:Voyage:ApiKey"] = Key });

        _ = services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        var (client, real) = Assert.Single(hooks.Created);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(7), real.ConnectTimeout);
    }

    private static ServiceProvider Build(string provider, RecordingHooks hooks, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AI:Chat:Provider"] = provider,
            ["AI:Chat:Model"] = provider == "OpenAI" ? "gpt-test" : null,
            ["AI:Embeddings:Provider"] = "Fake",
            ["AI:Anthropic:ApiKey"] = Key,
            ["AI:OpenAI:ApiKey"] = Key,
            ["AI:Retry:MaxAttempts"] = "1",
            ["AI:Timeouts:ConnectSeconds"] = "7",
        };
        foreach (var (name, value) in extra ?? [])
        {
            settings[name] = value;
        }

        return AiTestServices.Build(settings, configure: services => services.AddSingleton<IProviderHttpHooks>(hooks));
    }

    private sealed class RecordingHooks(HttpMessageHandler stub) : IProviderHttpHooks
    {
        public List<(HttpClient Client, SocketsHttpHandler Real)> Created { get; } = [];

        public HttpMessageHandler Wrap(SocketsHttpHandler real) => stub;

        void IProviderHttpHooks.Created(HttpClient client, SocketsHttpHandler real) => Created.Add((client, real));
    }
}
