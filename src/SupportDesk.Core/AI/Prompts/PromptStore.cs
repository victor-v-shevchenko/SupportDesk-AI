using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SupportDesk.Core.AI.Prompts;

public interface IPromptStore
{
    /// <summary>The configured version of the prompt <paramref name="name"/> (<c>AI:Prompts:&lt;name&gt;</c>).</summary>
    PromptTemplate GetPrompt(string name);
}

/// <summary>
/// Loads <c>prompts/&lt;name&gt;.v&lt;N&gt;.md</c> for each entry in <c>AI:Prompts</c>, all at once on first use. Startup
/// validation (<see cref="AiOptionsValidator"/>) loads them too, so a missing or broken file stops the app before that.
/// </summary>
public sealed partial class PromptStore : IPromptStore
{
    private readonly Lazy<IReadOnlyDictionary<string, PromptTemplate>> _prompts;

    public PromptStore(IOptions<AiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        _prompts = new(() => LoadAll(value.ResolvedPromptsPath, value.Prompts));
    }

    public PromptTemplate GetPrompt(string name) =>
        _prompts.Value.TryGetValue(name, out var prompt)
            ? prompt
            : throw new KeyNotFoundException($"No prompt '{name}' is configured; add AI:Prompts:{name}.");

    public static IReadOnlyDictionary<string, PromptTemplate> LoadAll(string folder, IReadOnlyDictionary<string, string> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);

        return versions.ToDictionary(v => v.Key, v => Load(folder, v.Key, v.Value), StringComparer.Ordinal);
    }

    public static PromptTemplate Load(string folder, string name, string version)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(version);

        // The version is also a file name, so this keeps it from pointing outside the folder
        if (!VersionRegex().IsMatch(version) || !version.StartsWith(name + ".v", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"AI:Prompts:{name} is '{version}'; expected '{name}.v<N>', for example '{name}.v1'.");
        }

        var path = Path.Combine(folder, version + ".md");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"AI:Prompts:{name} is '{version}', but {path} doesn't exist.", path);
        }

        return PromptTemplate.Parse(version, File.ReadAllText(path));
    }

    [GeneratedRegex(@"^[a-z][a-z0-9-]*\.v[1-9][0-9]*\z")]
    private static partial Regex VersionRegex();
}
