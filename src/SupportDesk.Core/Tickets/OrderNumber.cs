using System.Text.RegularExpressions;

namespace SupportDesk.Core.Tickets;

/// <summary>Kestrel Outfitters order numbers: <c>KO-</c> followed by six digits (<c>KO-\d{6}</c>).</summary>
public static partial class OrderNumber
{
    public const int Length = 9;

    public static bool IsValid(string? value) => value is not null && OrderNumberRegex().IsMatch(value);

    // [0-9] rather than \d, which also matches other scripts' digits; \z rather than $, which allows a trailing newline
    [GeneratedRegex(@"^KO-[0-9]{6}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex OrderNumberRegex();
}
