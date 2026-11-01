using TokenUsage.Core;
using Xunit;

namespace TokenUsage.Core.Tests;

public sealed class RunActivityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "activity-tests-" + Guid.NewGuid().ToString("N"));

    public RunActivityTests() => Directory.CreateDirectory(Path.Combine(_root, "s1", "subagents"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string User(string promptId) =>
        """{"type":"user","promptId":""" + $"\"{promptId}\"" + ""","message":{"role":"user","content":"secret"}}""";

    private static string Tools(params (string Id, string Name, string Input)[] calls) =>
        """{"type":"assistant","message":{"content":[""" + string.Join(",", calls.Select(c =>
            $"{{\"type\":\"tool_use\",\"id\":\"{c.Id}\",\"name\":\"{c.Name}\",\"input\":{c.Input}}}")) + "]}}";

    [Fact]
    public void Counts_tools_and_skills_per_agent_and_finds_who_started_whom()
    {
        var main = Path.Combine(_root, "s1.jsonl");
        File.WriteAllLines(main, [
            User("p0"), Tools(("x", "Bash", "{}")),                       // an earlier run, not counted
            User("p1"), Tools(("t1", "Skill", """{"skill":"workflow","args":"secret"}"""), ("t2", "Agent", "{}")),
            Tools(("t3", "Read", "{}")),
        ]);
        var sub = Path.Combine(_root, "s1", "subagents");
        File.WriteAllLines(Path.Combine(sub, "agent-a.jsonl"), [Tools(("t4", "Grep", "{}"), ("t5", "Grep", "{}"), ("t6", "Agent", "{}"))]);
        File.WriteAllText(Path.Combine(sub, "agent-a.meta.json"), """{"agentType":"planner","toolUseId":"t2","spawnDepth":1}""");
        File.WriteAllLines(Path.Combine(sub, "agent-b.jsonl"), [Tools(("t7", "Bash", "{}"))]);
        File.WriteAllText(Path.Combine(sub, "agent-b.meta.json"), """{"agentType":"qa","toolUseId":"t6","spawnDepth":2}""");

        var list = RunActivity.Read(main, ["p1"], ["b", "a"]);
        var activity = list.ToDictionary(a => a.AgentId ?? "main");

        Assert.Equal(["main", "a", "b"], list.Select(a => a.AgentId ?? "main"));   // in the order they were started

        Assert.Equal(new Dictionary<string, int> { ["Skill"] = 1, ["Agent"] = 1, ["Read"] = 1 }, activity["main"].Tools);
        Assert.Equal(new Dictionary<string, int> { ["workflow"] = 1 }, activity["main"].Skills);
        Assert.Equal(2, activity["a"].Tools["Grep"]);
        Assert.Null(activity["a"].ParentAgentId);
        Assert.Equal("a", activity["b"].ParentAgentId);
        Assert.Equal(2, activity["b"].Depth);
    }

    private static string Listing(string[] removed, params string[] added) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "attachment",
            attachment = new { type = "agent_listing_delta", addedTypes = added.Select(l => l[2..l.IndexOf(':')]), addedLines = added, removedTypes = removed },
        });

    [Fact]
    public void Gives_each_agent_the_tools_its_type_was_listed_with_when_the_run_started()
    {
        var main = Path.Combine(_root, "s1.jsonl");
        File.WriteAllLines(main, [
            Listing([], "- planner: Plans. (Tools: Read, Grep)", "- qa: Reviews. (Tools: *)", "- old: Gone. (Tools: Bash)"),
            Listing(["old"], "- Explore: Searches. (Tools: All tools except Edit, Write)"),
            User("p1"), Tools(("t1", "Agent", "{}"), ("t2", "Agent", "{}"), ("t3", "Agent", "{}")),
            User("p2"),
            Listing([], "- planner: Changed later. (Tools: Bash)"),         // after the run, not used
        ]);
        var sub = Path.Combine(_root, "s1", "subagents");
        foreach (var (id, type, call) in new[] { ("a", "planner", "t1"), ("b", "Explore", "t2"), ("c", "old", "t3") })
        {
            File.WriteAllLines(Path.Combine(sub, $"agent-{id}.jsonl"), [Tools(("x" + id, "Read", "{}"))]);
            File.WriteAllText(Path.Combine(sub, $"agent-{id}.meta.json"), $$"""{"agentType":"{{type}}","toolUseId":"{{call}}"}""");
        }

        var activity = RunActivity.Read(main, ["p1"], ["a", "b", "c"]).ToDictionary(a => a.AgentId ?? "main");

        Assert.Equal(["Read", "Grep"], activity["a"].Allowed!.Names);
        Assert.False(activity["a"].Allowed!.Allows("Bash"));
        Assert.True(activity["b"].Allowed!.All);
        Assert.True(activity["b"].Allowed!.Allows("Bash"));
        Assert.False(activity["b"].Allowed!.Allows("Edit"));
        Assert.Null(activity["c"].Allowed);                                 // removed from the listing
        Assert.Null(activity["main"].Allowed);
        Assert.Empty(AllowedTools.FromListingLine("- qa: x (Tools: *)")!.Names);
        Assert.Null(AllowedTools.FromListingLine("- qa: no tools part"));
    }

    [Fact]
    public void A_missing_log_gives_an_empty_main_session()
    {
        var activity = RunActivity.Read(Path.Combine(_root, "gone.jsonl"), ["p1"], ["a"]);
        Assert.Empty(Assert.Single(activity).Tools);
    }
}
