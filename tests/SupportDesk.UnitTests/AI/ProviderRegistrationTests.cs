using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Api.AI;
using SupportDesk.Core.AI;

namespace SupportDesk.UnitTests.AI;

public class ProviderRegistrationTests
{
    private const string SecretKey = "sk-test-0123456789-must-not-leak";

    [Theory]
    [InlineData("Fake", "fake")]
    [InlineData("Ollama", "ollama")]
    [InlineData("Anthropic", "anthropic")]
    [InlineData("OpenAI", "openai")]
    public void Each_chat_provider_resolves_to_its_client_inside_the_pipeline(string provider, string providerName)
    {
        using var services = AiTestServices.Build(new Dictionary<string, string?>
        {
            ["AI:Chat:Provider"] = provider,
            ["AI:Chat:Model"] = provider == "OpenAI" ? "gpt-test" : null,
            ["AI:Anthropic:ApiKey"] = SecretKey,
            ["AI:OpenAI:ApiKey"] = SecretKey,
        });
        services.ValidateOnStart();

        var client = services.GetRequiredService<IChatClient>();

        Assert.Equal(providerName, client.GetService<ChatClientMetadata>()?.ProviderName, ignoreCase: true);
        Assert.NotNull(client.GetService<FunctionInvokingChatClient>());
        Assert.NotNull(client.GetService<ErrorMappingMiddleware>());
        Assert.Equal(provider == "Anthropic", client.GetService<AnthropicOptionsMiddleware>() is not null);
    }

    [Fact]
    public void The_pipeline_is_the_only_chat_client_registered()
    {
        var services = new ServiceCollection();

        services.AddSupportDeskAi();

        var registration = Assert.Single(services, s => s.ServiceType == typeof(IChatClient));
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
    }

    [Theory]
    [InlineData("Fake", "fake")]
    [InlineData("Ollama", "ollama")]
    [InlineData("Voyage", "voyage")]
    [InlineData("OpenAI", "openai")]
    public void Each_embedding_provider_resolves_to_its_generator(string provider, string providerName)
    {
        using var services = AiTestServices.Build(new Dictionary<string, string?>
        {
            ["AI:Embeddings:Provider"] = provider,
            ["AI:Voyage:ApiKey"] = SecretKey,
            ["AI:OpenAI:ApiKey"] = SecretKey,
        });
        services.ValidateOnStart();

        var generator = services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        Assert.Equal(providerName, generator.GetService<EmbeddingGeneratorMetadata>()?.ProviderName, ignoreCase: true);
    }

    [Theory]
    [InlineData("AI:Chat:Provider", "Gemini")]
    [InlineData("AI:Embeddings:Provider", "Cohere")]
    public void An_unknown_provider_fails_at_startup(string setting, string value)
    {
        using var services = AiTestServices.Build(new Dictionary<string, string?> { [setting] = value });

        var error = Assert.Throws<InvalidOperationException>(services.ValidateOnStart);

        Assert.Contains(setting, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_number_outside_the_enum_fails_at_startup()
    {
        var error = AiTestServices.StartupError(new Dictionary<string, string?> { ["AI:Chat:Provider"] = "7" });

        Assert.Contains("AI:Chat:Provider", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AI:Chat:Provider", "Anthropic", "AI:Anthropic:ApiKey")]
    [InlineData("AI:Chat:Provider", "OpenAI", "AI:OpenAI:ApiKey")]
    [InlineData("AI:Embeddings:Provider", "OpenAI", "AI:OpenAI:ApiKey")]
    [InlineData("AI:Embeddings:Provider", "Voyage", "AI:Voyage:ApiKey")]
    public void A_missing_api_key_fails_at_startup_naming_the_setting(string setting, string provider, string keySetting)
    {
        var error = AiTestServices.StartupError(new Dictionary<string, string?>
        {
            [setting] = provider,
            ["AI:Chat:Model"] = "some-model",
            [keySetting] = "  ",
        });

        Assert.Contains($"{keySetting} is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_api_key_is_not_logged_or_put_in_startup_errors()
    {
        using var logs = new CapturingLoggerProvider();
        var settings = new Dictionary<string, string?>
        {
            ["AI:Chat:Provider"] = "Anthropic",
            ["AI:Anthropic:ApiKey"] = SecretKey,
            ["AI:Embeddings:Dimensions"] = "768",
        };

        using (var failing = AiTestServices.Build(settings, logger: logs))
        {
            var error = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(failing.ValidateOnStart);
            Assert.DoesNotContain(SecretKey, error.ToString(), StringComparison.Ordinal);
        }

        settings.Remove("AI:Embeddings:Dimensions");
        using (var services = AiTestServices.Build(settings, logger: logs))
        {
            services.ValidateOnStart();
            Assert.NotNull(services.GetRequiredService<IChatClient>());
        }

        Assert.DoesNotContain(logs.Messages, m => m.Contains(SecretKey, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("AI:Chat:Provider")]
    [InlineData("AI:Embeddings:Provider")]
    public void The_fake_provider_fails_at_startup_in_Production(string setting)
    {
        var error = AiTestServices.StartupError(new Dictionary<string, string?> { [setting] = "Fake" }, environment: "Production");

        Assert.Contains($"{setting} is Fake, which isn't allowed in Production", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void The_fake_provider_starts_outside_Production(string environment)
    {
        using var services = AiTestServices.Build(
            new Dictionary<string, string?> { ["AI:Chat:Provider"] = "Fake", ["AI:Embeddings:Provider"] = "Fake" },
            environment);

        services.ValidateOnStart();
    }

    [Fact]
    public void Ollama_needs_an_endpoint_only_when_it_is_a_provider()
    {
        var error = AiTestServices.StartupError(new Dictionary<string, string?> { ["AI:Ollama:Endpoint"] = "" });
        using var withoutOllama = AiTestServices.Build(new Dictionary<string, string?>
        {
            ["AI:Ollama:Endpoint"] = "",
            ["AI:Chat:Provider"] = "Fake",
            ["AI:Embeddings:Provider"] = "Fake",
        });

        Assert.Contains("AI:Ollama:Endpoint must be an absolute http or https URL", error.Message, StringComparison.Ordinal);
        withoutOllama.ValidateOnStart();
    }

    [Fact]
    public void OpenAI_needs_a_model()
    {
        var error = AiTestServices.StartupError(new Dictionary<string, string?> { ["AI:Chat:Provider"] = "OpenAI", ["AI:OpenAI:ApiKey"] = SecretKey });

        Assert.Contains("AI:Chat:Model is required", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AI:Embeddings:Dimensions", "768", "AI:Embeddings:Dimensions")]
    [InlineData("AI:Timeouts:ConnectSeconds", "0", "AI:Timeouts:ConnectSeconds")]
    [InlineData("AI:Ollama:Endpoint", "file:///etc/passwd", "AI:Ollama:Endpoint")]
    [InlineData("AI:Anthropic:Effort:Reply", "None", "AI:Anthropic:Effort:Reply")]
    public void Invalid_settings_fail_at_startup(string setting, string value, string expected)
    {
        var error = AiTestServices.StartupError(new Dictionary<string, string?> { [setting] = value });

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fake_scenarios_load_from_the_configured_path_and_a_broken_file_fails_at_startup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scenarios-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "scenarios": [] }""");
        try
        {
            var error = AiTestServices.StartupError(new Dictionary<string, string?>
            {
                ["AI:Chat:Provider"] = "Fake",
                ["AI:Fake:ScenariosPath"] = path,
            });

            Assert.Contains("there is no 'default' scenario", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
