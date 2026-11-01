using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TokenUsage.Core;

/// <summary>An agent file as it was when an agent of its type ran: where it was, and its whole text.</summary>
public sealed record AgentDefinition(string AgentType, string Path, string Content, DateTimeOffset FirstSeen);

/// <summary>
/// Finds the agent file a subagent was started from. Claude Code's logs only name the agent type, so the file
/// is looked up the way Claude Code does: the project's .claude\agents (from the session's working directory
/// up), then the user's own agents next to the projects folder. Built-in agents have no file.
/// </summary>
public static class AgentDefinitions
{
    /// <summary>
    /// How recently a subagent's log must have been written for its agent file to be taken as the one it ran
    /// with. Older logs, met when everything is imported at first start, get no definition, since the file
    /// may have changed since.
    /// </summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);

    /// <summary>The file for the agent type, or null when there is none (a built-in agent) or it can't be read.</summary>
    public static (string Path, string Content)? Find(string agentLogPath, string agentType)
    {
        var dirs = new List<string>();
        // Up to, not including, your home folder: its .claude is the user level, found from the config folder below.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (WorkingDirectory(agentLogPath) is string cwd)
            for (var dir = new DirectoryInfo(cwd); dir is not null && !Path.TrimEndingDirectorySeparator(dir.FullName).Equals(Path.TrimEndingDirectorySeparator(home), StringComparison.OrdinalIgnoreCase); dir = dir.Parent)
                dirs.Add(Path.Combine(dir.FullName, ".claude", "agents"));
        // projects\<project>\<session>\subagents\agent-<id>.jsonl: the config folder holds both projects and agents.
        var configDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(agentLogPath))))));
        if (configDir is not null) dirs.Add(Path.Combine(configDir, "agents"));

        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            if (FindIn(dir, agentType) is { } found) return found;
        return null;
    }

    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    // Usually the file is named after the agent; otherwise its frontmatter "name:" says which it is.
    private static (string, string)? FindIn(string dir, string agentType)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var named = Path.Combine(dir, agentType + ".md");
            if (File.Exists(named)) return (named, File.ReadAllText(named));
            foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                if (FrontmatterName(text) == agentType) return (file, text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static string? FrontmatterName(string text)
    {
        using var reader = new StringReader(text);
        if (reader.ReadLine()?.Trim() != "---") return null;
        for (var line = reader.ReadLine(); line is not null && line.Trim() != "---"; line = reader.ReadLine())
            if (line.StartsWith("name:", StringComparison.Ordinal)) return line[5..].Trim().Trim('"', '\'');
        return null;
    }

    // Every record in a session log carries the working directory; the first one is enough.
    private static string? WorkingDirectory(string logPath)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            for (var i = 0; i < 20 && reader.ReadLine() is string line; i++)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (Json.String(doc.RootElement, "cwd") is { Length: > 0 } cwd) return cwd;
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
