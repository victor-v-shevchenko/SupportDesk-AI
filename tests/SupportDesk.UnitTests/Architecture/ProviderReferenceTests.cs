using System.Xml.Linq;
using SupportDesk.Core.AI;
using SupportDesk.Data;

namespace SupportDesk.UnitTests.Architecture;

/// <summary>Only the API's composition root may reference provider SDKs (PLAN.md 2, CLAUDE.md LLM rules).</summary>
public class ProviderReferenceTests
{
    private static readonly string[] ProviderPackages = ["Anthropic", "OpenAI", "Microsoft.Extensions.AI.OpenAI", "OllamaSharp", "System.ClientModel"];

    private static readonly string[] ProjectFolders = ["src", "tools"];

    private static readonly string[] ProviderAssemblies = ["Anthropic", "OpenAI", "Microsoft.Extensions.AI.OpenAI", "OllamaSharp", "System.ClientModel"];

    [Fact]
    public void No_project_outside_the_api_references_a_provider_package()
    {
        var root = RepositoryRoot();
        var projects = ProjectFolders
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.GetFiles(folder, "*.csproj", SearchOption.AllDirectories))
            .ToList();

        Assert.Contains(projects, p => Path.GetFileName(p) == "SupportDesk.Api.csproj");
        foreach (var project in projects.Where(p => Path.GetFileName(p) != "SupportDesk.Api.csproj"))
        {
            var packages = XDocument.Load(project).Descendants("PackageReference").Select(r => (string?)r.Attribute("Include"));
            Assert.DoesNotContain(packages, p => ProviderPackages.Contains(p, StringComparer.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_api_is_the_project_that_references_them()
    {
        var api = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "SupportDesk.Api", "SupportDesk.Api.csproj"));

        var packages = api.Descendants("PackageReference").Select(r => (string?)r.Attribute("Include")).ToList();

        Assert.Contains("Anthropic", packages);
        Assert.Contains("OllamaSharp", packages);
        Assert.Contains("Microsoft.Extensions.AI.OpenAI", packages);
    }

    [Fact]
    public void Core_and_data_assemblies_do_not_reference_provider_assemblies_even_transitively()
    {
        foreach (var assembly in new[] { typeof(AiPipeline).Assembly, typeof(SupportDeskDbContext).Assembly })
        {
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name);
            Assert.DoesNotContain(references, r => ProviderAssemblies.Contains(r, StringComparer.OrdinalIgnoreCase));
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SupportDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("SupportDesk.slnx not found above the test folder.");
    }
}
