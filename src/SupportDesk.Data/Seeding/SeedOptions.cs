using System.ComponentModel.DataAnnotations;

namespace SupportDesk.Data.Seeding;

public sealed class SeedOptions
{
    /// <summary>The seed folder; a relative path is resolved from the app's base directory, where the build copies <c>seed/</c>.</summary>
    [Required]
    public string Path { get; set; } = "seed";

    public string ResolvedPath => System.IO.Path.GetFullPath(Path, AppContext.BaseDirectory);
}
