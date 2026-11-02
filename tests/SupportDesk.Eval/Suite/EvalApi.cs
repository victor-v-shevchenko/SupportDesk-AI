using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportDesk.Data;

namespace SupportDesk.Eval.Suite;

/// <summary>
/// The API in memory, in Development, on the run's own database, with the configured chat provider. Retrieval embeds
/// with the committed vectors (<see cref="FixtureEmbeddingGenerator"/>), so only the chat model is real.
/// </summary>
public sealed class EvalApi(string connectionString, FixtureEmbeddingGenerator embeddings) : WebApplicationFactory<Program>
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public SupportDeskDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SupportDeskDbContext>().UseNpgsql(connectionString, npgsql => npgsql.UseVector()).Options);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Added last, so they win over the dev container's ConnectionStrings__SupportDesk variable; the chat settings stay as configured
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SupportDesk"] = connectionString,
            ["AI:Embeddings:Provider"] = "Fake",
            ["AI:Embeddings:Model"] = embeddings.Model,
        }));
        builder.ConfigureTestServices(services => services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddings));
    }
}

/// <summary>One server-sent event: its name and its JSON data.</summary>
public sealed record ServerSentEvent(string Event, JsonElement Data);

public static class ServerSentEvents
{
    /// <summary>Reads every event of a stream until it ends.</summary>
    public static async Task<IReadOnlyList<ServerSentEvent>> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var events = new List<ServerSentEvent>();
        using var reader = new StreamReader(stream);
        string? name = null;
        var data = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Count > 0)
                {
                    events.Add(new ServerSentEvent(name ?? "message", JsonDocument.Parse(string.Join('\n', data)).RootElement.Clone()));
                }

                name = null;
                data.Clear();
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Add(line["data:".Length..].TrimStart());
            }
        }

        return events;
    }
}
