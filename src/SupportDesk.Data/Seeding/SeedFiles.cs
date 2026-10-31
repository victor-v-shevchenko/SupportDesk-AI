using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportDesk.Data.Seeding;

/// <summary>Reads the files in <c>seed/</c>. Parsing is strict, so a typo in a seed file fails loudly instead of seeding partial data.</summary>
public static class SeedFiles
{
    public const string TicketsFile = "tickets.json";
    public const string OrdersFile = "orders.json";
    public const string ArticlesFolder = "articles";

    private const string FrontMatterFence = "---";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<IReadOnlyList<SeedTicket>> ReadTicketsAsync(string seedPath, CancellationToken cancellationToken) =>
        await ReadJsonAsync<SeedTicket>(Path.Combine(seedPath, TicketsFile), cancellationToken);

    public static async Task<IReadOnlyList<SeedOrder>> ReadOrdersAsync(string seedPath, CancellationToken cancellationToken) =>
        await ReadJsonAsync<SeedOrder>(Path.Combine(seedPath, OrdersFile), cancellationToken);

    public static async Task<IReadOnlyList<SeedArticle>> ReadArticlesAsync(string seedPath, CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(Path.Combine(seedPath, ArticlesFolder), "*.md").Order(StringComparer.Ordinal);
        var articles = new List<SeedArticle>();
        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken);
            articles.Add(ParseArticle(Path.GetFileName(file), text));
        }

        return articles;
    }

    /// <summary>Parses a front matter block (<c>slug</c> and <c>title</c>, between <c>---</c> lines) and the Markdown body after it.</summary>
    public static SeedArticle ParseArticle(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (lines[0].Trim() != FrontMatterFence)
        {
            throw new InvalidDataException($"{fileName}: expected front matter starting with '---' on the first line.");
        }

        var end = Array.FindIndex(lines, 1, line => line.Trim() == FrontMatterFence);
        if (end < 0)
        {
            throw new InvalidDataException($"{fileName}: front matter has no closing '---'.");
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines[1..end].Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new InvalidDataException($"{fileName}: front matter line '{line}' isn't 'key: value'.");
            }

            fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var body = string.Join('\n', lines[(end + 1)..]).Trim();
        if (body.Length == 0)
        {
            throw new InvalidDataException($"{fileName}: the article has no body.");
        }

        return new SeedArticle(Required(fields, "slug", fileName), Required(fields, "title", fileName), body);
    }

    private static string Required(Dictionary<string, string> fields, string key, string fileName) =>
        fields.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : throw new InvalidDataException($"{fileName}: front matter is missing '{key}'.");

    private static async Task<IReadOnlyList<T>> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<T>>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"{path} is empty.");
    }
}
