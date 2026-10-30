using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Telemetrix.Contracts;

// A label names a registered transformation; it is never a path or executable (ADR-0017).
public static partial class TransformationLabel
{
    public const string Pattern = "^[a-z0-9][a-z0-9-]{0,62}$";

    public static bool IsValid([NotNullWhen(true)] string? label) => label is not null && Regex().IsMatch(label);

    // \z instead of $ so a trailing newline cannot pass.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Regex();
}
