using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SupportDesk.Api.AI;
using SupportDesk.Core.AI;
using SupportDesk.Core.Retrieval;
using SupportDesk.Data.Seeding;

namespace SupportDesk.EmbedFixtures;

/// <summary>
/// Regenerates <c>tests/fixtures/embeddings/</c> with the configured embedding provider (the API's appsettings.json, user
/// secrets and <c>AI__…</c> variables), from the repository's <c>seed/articles</c> and retrieval questions. The committed
/// fixtures are made with Ollama's <c>mxbai-embed-large</c>, the default.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);
        builder.Services.AddSupportDeskAi();
        using var host = builder.Build();

        var root = FindRepositoryRoot();
        var options = host.Services.GetRequiredService<IOptions<AiOptions>>().Value.Embeddings;
        var batchSize = (builder.Configuration.GetSection(RetrievalOptions.SectionName).Get<RetrievalOptions>() ?? new()).EmbeddingBatchSize;
        var generator = host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        var articles = await SeedFiles.ReadArticlesAsync(Path.Combine(root, "seed"), cancel.Token);
        var questions = await EmbeddingFixtures.ReadQuestionsAsync(Path.Combine(root, EmbeddingFixtures.RetrievalQuestionsPath), cancel.Token);
        Console.WriteLine($"Embedding {articles.Count} articles and {questions.Count} questions with {options.Provider} {options.ResolvedModel}...");

        var (articleFile, questionFile) = await EmbeddingFixtures.BuildAsync(
            articles, questions, generator, options.ResolvedModel, options.Dimensions, batchSize, cancel.Token);

        var folder = Path.Combine(root, EmbeddingFixtures.FolderPath);
        await EmbeddingFixtures.WriteAsync(Path.Combine(folder, EmbeddingFixtures.ArticlesFile), articleFile, cancel.Token);
        await EmbeddingFixtures.WriteAsync(Path.Combine(folder, EmbeddingFixtures.QuestionsFile), questionFile, cancel.Token);
        Console.WriteLine($"Wrote {articleFile.Items.Count} chunks and {questionFile.Items.Count} questions to {EmbeddingFixtures.FolderPath}.");
        return 0;
    }

    /// <summary>The folder with <c>SupportDesk.slnx</c>, searched upwards from the current directory.</summary>
    private static string FindRepositoryRoot()
    {
        for (var folder = new DirectoryInfo(Environment.CurrentDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "SupportDesk.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("Run this from inside the repository: SupportDesk.slnx wasn't found above the current directory.");
    }
}
