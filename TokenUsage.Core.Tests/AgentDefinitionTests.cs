using TokenUsage.Core;
using Xunit;

namespace TokenUsage.Core.Tests;

public sealed class AgentDefinitionTests : IDisposable
{
    private const string Session = "11111111-2222-3333-4444-555555555555";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "definition-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _projects, _subagents, _repo;
    private readonly UsageDatabase _db;

    public AgentDefinitionTests()
    {
        // _root plays the Claude config folder: projects\ for the logs and agents\ for your own agents.
        _projects = Path.Combine(_root, "projects");
        _subagents = Path.Combine(_projects, "C--repo", Session, "subagents");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_subagents);
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        Directory.CreateDirectory(Path.Combine(_repo, "src", ".keep"));
        _db = UsageDatabase.OpenForWriting(Path.Combine(_root, "token-usage.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string WriteAgent(string id, string type, string cwd)
    {
        File.WriteAllText(Path.Combine(_subagents, $"agent-{id}.meta.json"), $$"""{"agentType":"{{type}}"}""");
        var log = Path.Combine(_subagents, $"agent-{id}.jsonl");
        File.WriteAllLines(log, [System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "user", promptId = "P1", cwd, timestamp = "2026-11-01T08:00:00Z", message = new { role = "user", content = "x" },
        })]);
        return log;
    }

    private (string? Path, string? Content) Definition(string id)
    {
        var d = RunReport.LoadDefinition(_db, id);
        return (d?.Path, d?.Content);
    }

    private long Count(string sql)
    {
        using var cmd = _db.Command(sql);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Keeps_the_agent_file_a_fresh_subagent_ran_with_preferring_the_project_one()
    {
        var mine = Path.Combine(_root, "agents", "planner.md");
        File.WriteAllText(mine, "---\nname: planner\n---\nMine");
        File.WriteAllText(Path.Combine(_root, "agents", "checker.md"), "---\nname: qa\ntools: Read\n---\nReview it");
        Directory.CreateDirectory(Path.Combine(_repo, ".claude", "agents"));
        var project = Path.Combine(_repo, ".claude", "agents", "planner.md");
        File.WriteAllText(project, "---\nname: planner\n---\nProject");

        WriteAgent("a", "planner", Path.Combine(_repo, "src"));   // found from a subfolder of the project
        WriteAgent("b", "qa", _repo);                             // found by its frontmatter name
        WriteAgent("c", "Explore", _repo);                        // built in: no file
        new TranscriptImporter(_db).ImportAll(_projects);

        Assert.Equal((Path.GetFullPath(project), "---\nname: planner\n---\nProject"), Definition("a"));
        Assert.Equal("---\nname: qa\ntools: Read\n---\nReview it", Definition("b").Content);
        Assert.Null(Definition("c").Path);
    }

    [Fact]
    public void Stores_a_new_copy_only_when_the_file_changes_and_keeps_the_old_one_for_earlier_agents()
    {
        var file = Path.Combine(_root, "agents", "planner.md");
        File.WriteAllText(file, "v1");
        WriteAgent("a", "planner", _repo);
        WriteAgent("b", "planner", _repo);
        new TranscriptImporter(_db).ImportAll(_projects);

        File.WriteAllText(file, "v2");
        WriteAgent("c", "planner", _repo);
        File.AppendAllText(Path.Combine(_subagents, "agent-a.jsonl"), "{}\n");   // a grows again: keeps v1
        new TranscriptImporter(_db).ImportAll(_projects);

        Assert.Equal(2, Count("SELECT count(*) FROM agent_definitions"));
        Assert.Equal("v1", Definition("a").Content);
        Assert.Equal("v1", Definition("b").Content);
        Assert.Equal("v2", Definition("c").Content);
    }

    [Fact]
    public void Gives_no_definition_to_an_agent_whose_log_is_too_old_to_tell()
    {
        File.WriteAllText(Path.Combine(_root, "agents", "planner.md"), "today's text");
        var log = WriteAgent("a", "planner", _repo);
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow - AgentDefinitions.Freshness - TimeSpan.FromMinutes(1));
        new TranscriptImporter(_db).ImportAll(_projects);

        Assert.Null(Definition("a").Path);
        Assert.Equal(0, Count("SELECT count(*) FROM agent_definitions"));
    }
}
