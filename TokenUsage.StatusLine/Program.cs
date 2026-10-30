using System.Text;
using TokenUsage.Core;

// Claude Code's status line command. Reads the session JSON from stdin, prints the status line,
// and records limits and token usage. Run with --import-all to read every session log once.
// Run with --hook as a Claude Code hook: reads the hook JSON from stdin and records token usage only,
// for the VS Code extension and the desktop app, which run hooks but no status line.

if (args is ["--import-all"])
{
    UsagePaths.MigrateLegacyData();
    using var db = UsageDatabase.OpenForWriting(UsagePaths.DatabasePath);
    new TranscriptImporter(db).ImportAll(UsagePaths.ClaudeProjectsDir);
    Console.WriteLine($"Läste in sessionsloggarna i {UsagePaths.ClaudeProjectsDir} till {UsagePaths.DatabasePath}.");
    return;
}

var isHook = args is ["--hook"];

string raw;
using (var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
    raw = stdin.ReadToEnd();

// Hook input has transcript_path but no rate_limits, so the limits stay untouched.
var input = StatusInput.Parse(raw);
var now = DateTimeOffset.Now;

// A hook prints nothing: on exit code 0, Claude Code would only add the output to its transcript.
if (!isHook)
{
    using var stdout = Console.OpenStandardOutput();
    stdout.Write(Encoding.UTF8.GetBytes(StatusLineText.Format(input, now) + Environment.NewLine));
}

// Recording must never break the status line or the session; keep only the last error for troubleshooting.
try
{
    UsagePaths.MigrateLegacyData();
    using var db = UsageDatabase.OpenForWriting(UsagePaths.DatabasePath);
    if (!isHook) db.UpdateLimits(input, now);
    var importer = new TranscriptImporter(db);
    const long maxBytesPerRun = 16 * 1024 * 1024;   // a long backlog is read over several runs
    if (input.TranscriptPath is { Length: > 0 } transcript && File.Exists(transcript))
        importer.ImportSession(transcript, maxBytesPerRun);
    // Other sessions, including headless `claude -p` runs that have no status line of their own.
    importer.ImportAllIfDue(UsagePaths.ClaudeProjectsDir, TimeSpan.FromSeconds(60), now, maxBytesPerRun);
}
catch (Exception ex)
{
    try { File.WriteAllText(UsagePaths.ErrorLogPath, $"{now:o}{Environment.NewLine}{ex}"); }
    catch (IOException) { }
}
