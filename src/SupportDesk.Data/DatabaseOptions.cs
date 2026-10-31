using System.ComponentModel.DataAnnotations;

namespace SupportDesk.Data;

public sealed class DatabaseOptions
{
    /// <summary>Read from <c>ConnectionStrings:SupportDesk</c>; it carries credentials, so it never goes in appsettings.</summary>
    [Required]
    public string ConnectionString { get; set; } = "";

    /// <summary>How long the health check waits for the database.</summary>
    [Range(1, 60)]
    public int HealthCheckTimeoutSeconds { get; set; } = 3;
}
