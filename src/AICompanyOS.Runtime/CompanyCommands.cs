namespace AICompanyOS.Runtime;

public sealed record CompanyCommandRequest(string Text);
public sealed record ResolvedCompanyCommand(string Namespace, string Action, string? NodeId, string[] Arguments, bool Mutating);
public static class CompanyCommandParser
{
    public static ResolvedCompanyCommand Resolve(string text)
    {
        if (text is null || text.Length is < 2 or > 512 || !text.StartsWith('/') || text.Any(char.IsControl) || SourceSafety.ContainsKnownSecret(text)) throw new ArgumentException("Use a bounded slash command; commands are never shell text.");
        var parts = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries); if (parts.Length == 0) throw new ArgumentException("Missing command.");
        var name = parts[0]; var explicitNamespace = name.Contains(':') ? name[..name.IndexOf(':')] : null; var arguments = parts.Skip(1).ToArray();
        if (name.StartsWith("role:", StringComparison.Ordinal)) name = name[5..];
        if (name.StartsWith("skill:", StringComparison.Ordinal)) name = name[6..];
        var node = name switch { "ba" or "spec" => "ba", "leader" or "plan" or "assign" => "leader", "dev" or "cook" or "implement" => "dev", "tester" or "test" => "tester", "soc" or "security-review" => "soc", "qa" or "check-policy" or "acceptance" => "qa", "subpm" or "report" => "subpm", _ => null };
        if (node is not null && arguments.Length == 0)
        {
            var resolvedNamespace = SoftwareContracts.Roles.Contains(name) ? "role" : "skill";
            if (explicitNamespace is not null && explicitNamespace != resolvedNamespace) throw new ArgumentException("The explicit namespace does not contain that command.");
            return new(resolvedNamespace, "inspect-node", node, [], false);
        }
        if (name is "status" or "trace" or "budget" && arguments.Length == 0) return new("query", name, null, [], false);
        if (name is "pause" or "resume" or "step" or "cancel" or "retry" && arguments.Length == 0) return new("runtime", name, null, [], true);
        if ((name == "run" && arguments is ["software-delivery"] or ["delivery-default"]) || name == "workflow:software-delivery" && arguments.Length == 0) return new("workflow", "create-run", null, [], true);
        if (name == "handoff" && arguments is [var target] && target.StartsWith('@') && Identifiers.IsValid(target[1..])) return new("runtime", "inspect-handoff", null, [target[1..]], false);
        throw new ArgumentException("Unknown command or arguments. Use /status, /trace, /budget, /spec, /plan, /cook, /test, /check-policy, /report, /handoff @agent, /run software-delivery or a workflow control.");
    }
}
