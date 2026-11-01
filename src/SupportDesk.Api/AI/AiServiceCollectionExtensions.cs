using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;
using SupportDesk.Core.AI;
using SupportDesk.Core.AI.Fake;
using SupportDesk.Core.AI.Prompts;
using SupportDesk.Core.AI.Usage;

namespace SupportDesk.Api.AI;

/// <summary>
/// The composition root for model providers (PLAN.md 6.1, 6.2): the only code that references provider SDKs. Features
/// take <see cref="IChatClient"/> (the shared pipeline) and <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>.
/// </summary>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddSupportDeskAi(this IServiceCollection services)
    {
        services.AddOptions<AiOptions>()
            .BindConfiguration(AiOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AiOptions>, AiOptionsValidator>();
        services.AddSingleton<IValidateOptions<AiOptions>, ProviderOptionsValidator>();
        services.AddOptions<ProviderKeys>()
            .Configure<IConfiguration>((keys, configuration) =>
            {
                var read = ProviderKeys.From(configuration);
                keys.Anthropic = read.Anthropic;
                keys.OpenAI = read.OpenAI;
                keys.Voyage = read.Voyage;
            });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IPromptStore, PromptStore>();
        services.AddSingleton<PriceTable>();
        services.AddSingleton<ModelCallBuffer>();
        services.AddSingleton<IModelCallSink>(provider => provider.GetRequiredService<ModelCallBuffer>());
        services.AddHostedService<ModelCallWriter>();
        services.AddSingleton(provider => AiPipeline.Build(CreateChatProvider(provider), CreateErrorClassifier(provider), provider));
        services.AddSingleton(CreateEmbeddingGenerator);
        return services;
    }

    private static IChatClient CreateChatProvider(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<AiOptions>>().Value;
        var keys = services.GetRequiredService<IOptions<ProviderKeys>>().Value;
        var model = options.Chat.ResolvedModel!;

        return options.Chat.Provider switch
        {
            ChatProvider.Fake => new FakeChatClient(FakeScenarioSet.Load(options.Fake.ResolvedScenariosPath), services.GetRequiredService<TimeProvider>()),
            ChatProvider.Ollama => new OllamaApiClient(CreateHttpClient(options, options.Ollama.Endpoint), model),
            ChatProvider.Anthropic => new AnthropicOptionsMiddleware(
                new AnthropicClient(new ClientOptions
                {
                    ApiKey = ProviderKeys.Require(keys.Anthropic, ProviderKeys.AnthropicSetting),
                    HttpClient = CreateHttpClient(options),
                    // Only RetryTimeoutMiddleware retries (PLAN.md 6.2)
                    MaxRetries = 0,
                }).AsIChatClient(model),
                options.Anthropic.Effort),
            ChatProvider.OpenAI => CreateOpenAIClient(options, keys).GetChatClient(model).AsIChatClient(),
            _ => throw new InvalidOperationException($"AI:Chat:Provider '{options.Chat.Provider}' isn't supported."),
        };
    }

    private static IModelErrorClassifier? CreateErrorClassifier(IServiceProvider services) =>
        services.GetRequiredService<IOptions<AiOptions>>().Value.Chat.Provider switch
        {
            ChatProvider.Anthropic => new AnthropicErrorClassifier(),
            ChatProvider.OpenAI => new OpenAIErrorClassifier(),
            ChatProvider.Ollama => new OllamaErrorClassifier(),
            ChatProvider.Fake => new FakeErrorClassifier(),
            _ => null,
        };

    private static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<AiOptions>>().Value;
        var keys = services.GetRequiredService<IOptions<ProviderKeys>>().Value;
        var embeddings = options.Embeddings;

        return embeddings.Provider switch
        {
            EmbeddingProvider.Fake => new FakeEmbeddingGenerator(embeddings.Dimensions),
            EmbeddingProvider.Ollama => new OllamaApiClient(CreateHttpClient(options, options.Ollama.Endpoint), embeddings.ResolvedModel),
            EmbeddingProvider.Voyage => new VoyageEmbeddingGenerator(
                CreateHttpClient(options), ProviderKeys.Require(keys.Voyage, ProviderKeys.VoyageSetting), embeddings.ResolvedModel, embeddings.Dimensions),
            EmbeddingProvider.OpenAI => CreateOpenAIClient(options, keys).GetEmbeddingClient(embeddings.ResolvedModel).AsIEmbeddingGenerator(embeddings.Dimensions),
            _ => throw new InvalidOperationException($"AI:Embeddings:Provider '{embeddings.Provider}' isn't supported."),
        };
    }

    private static OpenAIClient CreateOpenAIClient(AiOptions options, ProviderKeys keys) =>
        new(new ApiKeyCredential(ProviderKeys.Require(keys.OpenAI, ProviderKeys.OpenAISetting)), new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(CreateHttpClient(options)),
            // Only RetryTimeoutMiddleware retries (PLAN.md 6.2)
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        });

    /// <summary>
    /// One long-lived client per provider, with only a connect timeout (PLAN.md 6.2): how long a call may take is the
    /// pipeline's decision, since <see cref="HttpClient.Timeout"/> can't tell a slow stream from a stuck one.
    /// </summary>
    private static HttpClient CreateHttpClient(AiOptions options, Uri? baseAddress = null) =>
        new(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(options.Timeouts.ConnectSeconds),
            // Long-lived, so DNS changes are picked up by recycling connections instead
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        })
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
}
