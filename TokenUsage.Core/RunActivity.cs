using System.Text.Json;

namespace TokenUsage.Core;

/// <summary>
/// What one agent (or the main session, AgentId null) did in a run: which agent started it, how deep it
/// was spawned, how often it called each tool and skill, and which tools its type was allowed. Read from
/// Claude Code's logs on request; nothing of it is stored, and no tool input other than a skill's name is read.
/// </summary>
public sealed record AgentActivity(
    string? AgentId, string? ParentAgentId, int Depth,
    IReadOnlyDictionary<string, int> Tools, IReadOnlyDictionary<string, int> Skills, AllowedTools? Allowed);

/// <summary>
/// The tools an agent type was allowed, as the session listed them when it ran: every tool, every tool
/// except the ones named, or only the ones named.
/// </summary>
public sealed record AllowedTools(bool All, IReadOnlyList<string> Names)
{
    /// <summary>Parses the "(Tools: …)" ending a line of Claude Code's agent listing; null without one.</summary>
    public static AllowedTools? FromListingLine(string line)
    {
        var start = line.LastIndexOf("(Tools: ", StringComparison.Ordinal);
        if (start < 0 || !line.EndsWith(')')) return null;
        var spec = line[(start + "(Tools: ".Length)..^1].Trim();
        if (spec == "*") return new(true, []);
        const string except = "All tools except ";
        var all = spec.StartsWith(except, StringComparison.Ordinal);
        var names = (all ? spec[except.Length..] : spec).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return new(all, names);
    }

    public bool Allows(string tool) => All != Names.Contains(tool);
}

public static class RunActivity
{
    /// <summary>
    /// The main session's activity in the given prompts, then each subagent's, read from the session's main
    /// log and its subagents' logs next to it. Logs that are gone are left out. The whole main log is read,
    /// since the agent listing that says what each agent type may use can come long before the run.
    /// </summary>
    public static IReadOnlyList<AgentActivity> Read(
        string mainLogPath, IReadOnlyCollection<string> promptIds, IReadOnlyCollection<string> agentIds)
    {
        // Which agent made each Agent call, so a subagent's .meta.json toolUseId leads to its parent.
        var spawnedBy = new Dictionary<string, string?>();
        var result = new List<AgentActivity>();

        var main = new Counter();
        // The agent types the session listed as of the run; changes to the listing after it don't apply.
        var listing = new Dictionary<string, AllowedTools?>();
        bool inRun = false, runOver = false;
        foreach (var record in Records(mainLogPath))
        {
            if (Json.String(record, "type") == "user" && Json.String(record, "promptId") is string id)
            {
                runOver |= inRun && !promptIds.Contains(id);
                inRun = promptIds.Contains(id);
            }
            if (inRun) main.Add(record, null, spawnedBy);
            if (!runOver) ApplyListing(record, listing);
        }
        result.Add(main.ToActivity(null, null, 0, null));

        var subagents = Path.Combine(Path.ChangeExtension(mainLogPath, null), "subagents");
        var metas = new List<(string AgentId, string? Type, string? ToolUseId, int Depth, Counter Counter)>();
        foreach (var agentId in agentIds)
        {
            var log = Path.Combine(subagents, $"agent-{agentId}.jsonl");
            if (!File.Exists(log)) continue;
            var counter = new Counter();
            foreach (var record in Records(log)) counter.Add(record, agentId, spawnedBy);
            var (type, toolUseId, depth) = ReadMeta(Path.ChangeExtension(log, ".meta.json"));
            metas.Add((agentId, type, toolUseId, depth, counter));
        }
        // In the order they were started, which is the order their Agent calls were made in.
        var order = spawnedBy.Keys.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        foreach (var (agentId, type, toolUseId, depth, counter) in metas.OrderBy(m => m.ToolUseId is not null && order.TryGetValue(m.ToolUseId, out var i) ? i : int.MaxValue))
        {
            var parent = toolUseId is not null ? spawnedBy.GetValueOrDefault(toolUseId) : null;
            result.Add(counter.ToActivity(agentId, parent, Math.Max(depth, 1), type is null ? null : listing.GetValueOrDefault(type)));
        }
        return result;
    }

    /// <summary>
    /// The task the subagent was given: the first message in its log, as its parent wrote it. Read on request
    /// and never stored, since it can hold anything from your prompt. Null once the log is gone.
    /// </summary>
    public static string? ReadAgentPrompt(string mainLogPath, string agentId)
    {
        var log = Path.Combine(Path.ChangeExtension(mainLogPath, null), "subagents", $"agent-{agentId}.jsonl");
        foreach (var record in Records(log))
        {
            if (Json.String(record, "type") != "user") continue;
            if (!Json.Object(record, "message").TryGetProperty("content", out var content)) return null;
            return content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : string.Join("\n\n", content.EnumerateArray().Where(b => Json.String(b, "type") == "text").Select(b => Json.String(b, "text")));
        }
        return null;
    }

    private sealed class Counter
    {
        private readonly Dictionary<string, int> _tools = [];
        private readonly Dictionary<string, int> _skills = [];

        public void Add(JsonElement record, string? agentId, Dictionary<string, string?> spawnedBy)
        {
            if (Json.String(record, "type") != "assistant") return;
            if (!Json.Object(record, "message").TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
            foreach (var block in content.EnumerateArray())
            {
                if (Json.String(block, "type") != "tool_use" || Json.String(block, "name") is not string name) continue;
                _tools[name] = _tools.GetValueOrDefault(name) + 1;
                if (name is "Agent" or "Task" && Json.String(block, "id") is string id) spawnedBy[id] = agentId;
                if (name == "Skill" && Json.String(Json.Object(block, "input"), "skill") is string skill)
                    _skills[skill] = _skills.GetValueOrDefault(skill) + 1;
            }
        }

        public AgentActivity ToActivity(string? agentId, string? parent, int depth, AllowedTools? allowed) =>
            new(agentId, parent, depth, _tools, _skills, allowed);
    }

    // An "agent_listing_delta" attachment adds and removes agent types; each added one comes with a line
    // "- type: description (Tools: …)".
    private static void ApplyListing(JsonElement record, Dictionary<string, AllowedTools?> listing)
    {
        var a = Json.Object(record, "attachment");
        if (Json.String(a, "type") != "agent_listing_delta") return;
        foreach (var type in Strings(a, "removedTypes")) listing.Remove(type);
        foreach (var line in Strings(a, "addedLines"))
        {
            var colon = line.IndexOf(':');
            if (!line.StartsWith("- ", StringComparison.Ordinal) || colon < 2) continue;
            listing[line[2..colon]] = AllowedTools.FromListingLine(line);
        }
    }

    private static IEnumerable<string> Strings(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
            : [];

    private static (string? Type, string? ToolUseId, int Depth) ReadMeta(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var m = doc.RootElement;
            return (Json.String(m, "agentType"), Json.String(m, "toolUseId"), (int)Json.Long(m, "spawnDepth"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return (null, null, 0); }
    }

    private static IEnumerable<JsonElement> Records(string path)
    {
        StreamReader reader;
        try
        {
            reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        using (reader)
        {
            while (true)
            {
                string? line;
                try { line = reader.ReadLine(); }
                catch (IOException) { yield break; }
                if (line is null) yield break;
                JsonElement record;
                try { using var doc = JsonDocument.Parse(line); record = doc.RootElement.Clone(); }
                catch (JsonException) { continue; }
                yield return record;
            }
        }
    }
}
