using System.Text.RegularExpressions;

namespace DevDbStudio.Services.Dbms;

public enum InstanceStatus { Unknown, Running, Stopped, NotFound }

public sealed record DbInstance
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string DbmsKey { get; init; } = "";
    public string ContainerName { get; init; } = "";
    public int HostPort { get; init; }
    public DateTime CreatedUtc { get; init; }
    public InstanceStatus Status { get; init; } = InstanceStatus.Unknown;
    public string DatabaseName { get; init; } = "master";
    public string Username { get; init; } = "sa";
    // v1: stored in plain text in the local JSON file. Do not expose beyond this machine.
    public string Password { get; init; } = "";
    public string VolumeName { get; init; } = "";
    public string ImageTag { get; init; } = "";
    public bool SharedNetwork { get; init; }
    public bool NetworkChangesSupported { get; init; }
}

public sealed record InstanceCreateRequest(string Name, string DbmsKey, int HostPort, string Password, string DatabaseName, string ImageTag, bool SharedNetwork = false);

public sealed record InstanceCreateResult(bool Success, DbInstance? Instance, string Message, string StdErr = "")
{
    public static InstanceCreateResult Fail(string message, string stderr = "") => new(false, null, message, stderr);
}

public sealed record InstanceActionResult(bool Success, string Message, string StdErr = "");

public sealed record QueryResult(bool IsSuccess, string StdOut, string StdErr, int ExitCode, TimeSpan Elapsed);

public static partial class InstanceValidation
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}\z")]
    private static partial Regex DbNameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z")]
    private static partial Regex TagRegex();

    public static IReadOnlyList<string> PasswordErrors(string? pw)
    {
        var e = new List<string>();
        pw ??= "";
        if (pw.Length < 8) e.Add("At least 8 characters.");
        if (pw.Length > 128) e.Add("At most 128 characters.");
        if (!pw.Any(char.IsUpper)) e.Add("At least one uppercase letter.");
        if (!pw.Any(char.IsLower)) e.Add("At least one lowercase letter.");
        if (!pw.Any(char.IsDigit)) e.Add("At least one digit.");
        if (!pw.Any(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && !char.IsControl(c))) e.Add("At least one symbol (for example ! # $ % _).");
        if (pw.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) e.Add("No spaces or control characters.");
        return e;
    }

    /// <summary>Generates a random password that satisfies <see cref="PasswordErrors"/> and is safe in shell, YAML and URLs.</summary>
    public static string GeneratePassword(int length = 20)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnopqrstuvwxyz", digits = "23456789", symbols = "_-!";
        var all = upper + lower + digits + symbols;
        length = Math.Clamp(length, 8, 128);
        var chars = new List<char>(length)
        {
            Pick(upper), Pick(lower), Pick(digits), Pick(symbols)
        };
        while (chars.Count < length) chars.Add(Pick(all));
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());

        static char Pick(string s) => s[System.Security.Cryptography.RandomNumberGenerator.GetInt32(s.Length)];
    }

    public static IReadOnlyList<string> Validate(InstanceCreateRequest r)
    {
        var e = new List<string>();
        var name = r.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 50) e.Add("Name must be 1-50 characters.");
        if (r.HostPort is < 1024 or > 65535) e.Add("Host port must be between 1024 and 65535.");
        if (!DbNameRegex().IsMatch(r.DatabaseName ?? "")) e.Add("Database name must start with a letter and contain only letters, digits and underscores (max 64).");
        if (!TagRegex().IsMatch(r.ImageTag ?? "")) e.Add("Image tag is not valid.");
        e.AddRange(PasswordErrors(r.Password).Select(p => $"Password: {p}"));
        return e;
    }
}
