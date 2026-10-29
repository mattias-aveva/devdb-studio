using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DevDbStudio.Services.Dbms;

public enum PreviewSeverity { Error, Warning, Info }

public sealed record PreviewIssue(PreviewSeverity Severity, string Message);

/// <summary>Values read back from an edited compose file. Null means "not present or unchanged".</summary>
public sealed record ComposeEdit(int? HostPort, string? Tag, string? Password, string? Database, IReadOnlyList<string> Ignored);

/// <summary>
/// Shows the container DevDb Studio is about to create as a docker-compose file, and reads edits back into the form.
/// The text is rendered from the provider's real <c>docker run</c> arguments, so it cannot drift from what is executed.
/// </summary>
public static class ComposePreview
{
    public const string PasswordMask = "********";
    private const string PlaceholderContainer = "dbapp-{key}-xxxxxxxx";
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Render(IDbmsProvider p, InstanceCreateRequest req, bool maskPassword)
    {
        var container = PlaceholderContainer.Replace("{key}", p.Key);
        var volume = container + "-data";
        var shown = req with { Password = maskPassword && req.Password.Length > 0 ? PasswordMask : req.Password };
        var args = p.BuildRunArgs(shown, Guid.Empty, container, volume);

        string? name = null, image = null;
        List<string> ports = [], volumes = [], command = [], networks = [];
        List<(string K, string V)> env = [], labels = [];

        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (image is not null) { command.Add(a); continue; }
            switch (a)
            {
                case "-d": break;
                case "--name": name = args[++i]; break;
                case "--network": networks.Add(args[++i]); break;
                case "-p": ports.Add(args[++i]); break;
                case "-v": volumes.Add(args[++i]); break;
                case "-e": env.Add(Split(args[++i])); break;
                case "--label": labels.Add(Split(args[++i])); break;
                default: image = a; break;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Preview of the container DevDb Studio creates (it runs `docker run`; this is the same thing as compose).");
        sb.AppendLine("services:");
        sb.AppendLine($"  {p.Key}:");
        sb.AppendLine($"    image: {Q(image ?? "")}");
        sb.AppendLine($"    container_name: {Q(name ?? "")}");
        sb.AppendLine("    ports:");
        foreach (var port in ports) sb.AppendLine($"      - {Q(port)}");
        sb.AppendLine("    environment:");
        foreach (var (k, v) in env) sb.AppendLine($"      {k}: {Q(v)}");
        sb.AppendLine("    volumes:");
        foreach (var v in volumes) sb.AppendLine($"      - {Q(v)}");
        sb.AppendLine("    labels:");
        foreach (var (k, v) in labels)
            sb.AppendLine($"      {k}: {Q(k == "dbapp.id" && v == Guid.Empty.ToString() ? "(generated on create)" : v)}");
        if (networks.Count > 0)
        {
            sb.AppendLine("    networks:");
            foreach (var network in networks) sb.AppendLine($"      - {Q(network)}");
        }
        if (command.Count > 0)
            sb.AppendLine($"    command: [{string.Join(", ", command.Select(Q))}]");
        sb.AppendLine("volumes:");
        sb.AppendLine($"  {volume}:");
        if (networks.Count > 0)
        {
            sb.AppendLine("networks:");
            foreach (var network in networks)
            {
                sb.AppendLine($"  {network}:");
                if (network == SharedContainerNetwork.NetworkName)
                    sb.AppendLine("    external: true");
                else
                    sb.AppendLine("    driver: bridge");
                sb.AppendLine($"    name: {Q(network)}");
            }
        }
        return sb.ToString();
    }

    /// <summary>Reads the form-editable values (host port, image tag, password, database) out of edited text.</summary>
    public static ComposeEdit Parse(IDbmsProvider p, InstanceCreateRequest current, string yaml, bool maskPassword)
    {
        int? port = null; string? tag = null, password = null, database = null;
        var section = "";
        foreach (var raw in yaml.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var indent = line.Length - line.TrimStart().Length;

            if (indent == 4 && trimmed.EndsWith(':')) { section = trimmed[..^1]; continue; }
            if (indent == 4 && trimmed.StartsWith("image:", StringComparison.Ordinal))
            {
                var image = Unq(trimmed["image:".Length..]);
                var prefix = p.ImageName + ":";
                if (image.StartsWith(prefix, StringComparison.Ordinal)) tag = image[prefix.Length..];
                continue;
            }
            if (section == "ports" && trimmed.StartsWith('-'))
            {
                var parts = Unq(trimmed[1..]).Split(':');
                if (parts.Length == 3 && int.TryParse(parts[1], out var hp)) port = hp;
                continue;
            }
            if (section == "environment" && indent == 6)
            {
                var (k, v) = SplitYaml(trimmed);
                if (k == p.PasswordEnvVar && !(maskPassword && v == PasswordMask)) password = v;
                else if (k == p.DatabaseEnvVar) database = v;
            }
        }

        var applied = current with
        {
            HostPort = port ?? current.HostPort,
            ImageTag = tag ?? current.ImageTag,
            Password = password ?? current.Password,
            DatabaseName = database ?? current.DatabaseName,
        };

        // Anything the form cannot express is not used when creating, so tell the user instead of hiding it.
        var known = Render(p, applied, maskPassword).Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet();
        var ignored = yaml.Replace("\r", "").Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !known.Contains(l) && !l.StartsWith(p.PasswordEnvVar + ":", StringComparison.Ordinal))
            .ToList();

        return new ComposeEdit(port, tag, password, database, ignored);
    }

    public static IReadOnlyList<PreviewIssue> Check(
        IDbmsProvider p, InstanceCreateRequest req, IEnumerable<DbInstance> existing, string? yaml, ComposeEdit? lastEdit)
    {
        var issues = new List<PreviewIssue>();
        foreach (var e in InstanceValidation.Validate(req)) issues.Add(new(PreviewSeverity.Error, e));

        var others = existing.ToList();
        if (others.Any(i => i.HostPort == req.HostPort))
            issues.Add(new(PreviewSeverity.Error, $"Host port {req.HostPort} is already used by another DevDb Studio instance."));
        var name = req.Name?.Trim() ?? "";
        if (name.Length > 0 && others.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            issues.Add(new(PreviewSeverity.Error, $"An instance named '{name}' already exists."));

        var tag = req.ImageTag?.Trim() ?? "";
        if (tag.Equals("latest", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("latest-", StringComparison.OrdinalIgnoreCase))
            issues.Add(new(PreviewSeverity.Warning, $"Tag '{tag}' floats: a later 'create' may pull a different version. Pin a version to keep instances reproducible."));
        if (tag.Length > 0 && tag != p.DefaultImageTag)
            issues.Add(new(PreviewSeverity.Info, $"Using tag '{tag}' instead of the tested default '{p.DefaultImageTag}'. It is pulled on first create and may be large."));

        if (yaml is not null)
        {
            foreach (var binding in PortBindings(yaml))
            {
                var parts = binding.Split(':');
                if (parts.Length == 3 && parts[0] != "127.0.0.1")
                    issues.Add(new(PreviewSeverity.Warning, $"Port binding '{binding}' would expose the database beyond this machine. DevDb Studio always binds to 127.0.0.1, so this part is not used."));
                else if (parts.Length == 2)
                    issues.Add(new(PreviewSeverity.Warning, $"Port binding '{binding}' has no host address (would listen on all interfaces). DevDb Studio always binds to 127.0.0.1, so this is not used."));
            }
        }

        if (lastEdit is { Ignored.Count: > 0 })
            foreach (var l in lastEdit.Ignored.Take(8))
                issues.Add(new(PreviewSeverity.Warning, $"Not used (cannot be changed here): {l}"));
        return issues;
    }

    private static IEnumerable<string> PortBindings(string yaml)
    {
        var section = "";
        foreach (var raw in yaml.Replace("\r", "").Split('\n'))
        {
            var t = raw.Trim();
            var indent = raw.Length - raw.TrimStart().Length;
            if (indent == 4 && t.EndsWith(':')) { section = t[..^1]; continue; }
            if (section == "ports" && t.StartsWith('-')) yield return Unq(t[1..]);
        }
    }

    private static (string K, string V) Split(string kv)
    {
        var i = kv.IndexOf('=');
        return i < 0 ? (kv, "") : (kv[..i], kv[(i + 1)..]);
    }

    private static (string K, string V) SplitYaml(string line)
    {
        var i = line.IndexOf(':');
        return i < 0 ? (line, "") : (line[..i].Trim(), Unq(line[(i + 1)..]));
    }

    private static string Q(string s) => JsonSerializer.Serialize(s, Json).Replace("$", "$$");

    // Reverse of Q for double-quoted values; plain values are taken as they are.
    private static string Unq(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            try { s = JsonSerializer.Deserialize<string>(s.Replace("$$", "$"), Json) ?? ""; return s; }
            catch (JsonException) { return s[1..^1].Replace("$$", "$"); }
        }
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') s = s[1..^1].Replace("''", "'");
        return s.Replace("$$", "$");
    }
}
