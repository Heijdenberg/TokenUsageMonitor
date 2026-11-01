using System.Windows;
using Microsoft.Data.Sqlite;
using TokenUsage.Core;

namespace TokenUsage.Monitor;

/// <summary>One run split by agent and model, opened by double-clicking it in the runs grid.</summary>
public partial class RunWindow : Window
{
    private readonly RunRow _run;

    public RunWindow(RunRow run)
    {
        InitializeComponent();
        _run = run;
        Heading.Text = run.Label;
        Details.Text = run.Details;
        Agents.ItemsSource = run.Agents;

        var activity = ActivityRow.Tree(run, run.ReadActivity(), LoadDefinitions(run));
        Activity.ItemsSource = activity;
        if (activity.Count == 0) ActivityCaption.Text = "Struktur, verktyg och skills: loggen finns inte längre.";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (MainWindow.UseSystemFrame(this)) WindowFrame.BorderThickness = new Thickness(0);
    }

    // The agent files kept when the subagents ran; none for agents imported before that was done.
    private static Dictionary<string, AgentDefinition> LoadDefinitions(RunRow run)
    {
        var result = new Dictionary<string, AgentDefinition>();
        try
        {
            using var db = UsageDatabase.OpenReadOnly(UsagePaths.DatabasePath);
            foreach (var id in run.Agents.Select(a => a.AgentId).OfType<string>().Distinct())
                if (RunReport.LoadDefinition(db, id) is { } definition) result[id] = definition;
        }
        catch (SqliteException) { }   // a database the status line hasn't upgraded yet has no definitions
        return result;
    }

    private void ShowDefinition_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ActivityRow { Definition: { } d } row) return;
        var details = $"{row.Name}  ·  {d.Path}  ·  sparad {d.FirstSeen.LocalDateTime.ToString("d MMM HH:mm", MainWindow.Swedish)}";
        new PromptWindow(d.Content, details, "Agentdefinition vid körningen", markdown: true) { Owner = this }.Show();
    }

    private void ShowAgentPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ActivityRow { AgentId: { } id } row) return;
        var prompt = _run.ReadAgentPrompt(id) ?? "(uppdraget finns inte längre i loggen)";
        new PromptWindow(prompt, row.Name, "Uppdraget från huvudsessionen", markdown: true) { Owner = this }.Show();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>An agent in the run's tree, with its tool calls as bars and its skills as tags.</summary>
public sealed class ActivityRow
{
    public required string? AgentId { get; init; }
    public Visibility SubagentVisibility => AgentId is null ? Visibility.Collapsed : Visibility.Visible;
    public required string Name { get; init; }
    public required Thickness Indent { get; init; }
    public required Visibility BranchVisibility { get; init; }
    public required string Summary { get; init; }
    /// <summary>The agent file as it was when the agent ran, if a copy was kept.</summary>
    public required AgentDefinition? Definition { get; init; }
    public bool HasDefinition => Definition is not null;
    public string DefinitionTip => HasDefinition
        ? "Agentfilen som den såg ut när agenten kördes"
        : "Ingen sparad definition: inbyggd agent, eller körd innan definitioner sparades";
    /// <summary>The tools the agent's type was allowed when it ran; null when the log doesn't say.</summary>
    public required string? Allowed { get; init; }
    public Visibility AllowedVisibility => Allowed is null ? Visibility.Collapsed : Visibility.Visible;
    public required IReadOnlyList<ToolBar> Tools { get; init; }
    public required IReadOnlyList<SkillTag> Skills { get; init; }

    public sealed record ToolBar(string Name, int Count, double Share, double Opacity);
    public sealed record SkillTag(string Text);

    /// <summary>The main session first, each agent under the one that started it.</summary>
    public static List<ActivityRow> Tree(RunRow run, IReadOnlyList<AgentActivity> activity, IReadOnlyDictionary<string, AgentDefinition> definitions)
    {
        var known = activity.Select(a => a.AgentId).OfType<string>().ToHashSet();
        // Agents whose parent isn't in this run hang under the main session.
        var children = activity.Where(a => a.AgentId is not null)
            .ToLookup(a => a.ParentAgentId is not null && known.Contains(a.ParentAgentId) ? a.ParentAgentId : "");
        var rows = new List<ActivityRow>();
        void Add(AgentActivity a, int level)
        {
            rows.Add(Create(run, a, level, a.AgentId is null ? null : definitions.GetValueOrDefault(a.AgentId)));
            foreach (var child in children[a.AgentId ?? ""]) Add(child, level + 1);
        }
        foreach (var root in activity.Where(a => a.AgentId is null)) Add(root, 0);
        return rows;
    }

    private static ActivityRow Create(RunRow run, AgentActivity a, int level, AgentDefinition? definition)
    {
        var calls = a.Tools.Values.Sum();
        var max = a.Tools.Count == 0 ? 1 : a.Tools.Values.Max();
        return new ActivityRow
        {
            AgentId = a.AgentId,
            Name = run.AgentName(a.AgentId),
            Definition = definition,
            Indent = new Thickness(Math.Max(level - 1, 0) * 22, 0, 0, 0),
            BranchVisibility = level > 0 ? Visibility.Visible : Visibility.Collapsed,
            Summary = "  " + (calls == 0 ? "inga verktygsanrop" : string.Format(MainWindow.Swedish, "{0:N0} verktygsanrop", calls)),
            Allowed = a.Allowed switch
            {
                null => null,
                { All: true, Names.Count: 0 } => "Får använda: alla verktyg",
                { All: true } => "Får använda: alla verktyg utom " + string.Join(", ", a.Allowed.Names),
                _ => "Får använda: " + string.Join(", ", a.Allowed.Names),
            },
            // Used tools as bars, then the ones it was allowed but never called, faded.
            Tools = a.Tools.OrderByDescending(t => t.Value).ThenBy(t => t.Key)
                .Select(t => new ToolBar(t.Key, t.Value, (double)t.Value / max, 1))
                .Concat(a.Allowed is { All: false } allowed
                    ? allowed.Names.Where(n => !a.Tools.ContainsKey(n)).Select(n => new ToolBar(n, 0, 0, 0.45))
                    : [])
                .ToList(),
            // A run started with a slash command (often a skill) shows it on the main session.
            Skills = (a.AgentId is null && run.Command is string command ? [new SkillTag(command)] : new List<SkillTag>())
                .Concat(a.Skills.OrderByDescending(s => s.Value).Select(s => new SkillTag(s.Value > 1 ? $"{s.Key} ×{s.Value}" : s.Key)))
                .ToList(),
        };
    }
}
