namespace TokenUsage.Core;

public static class UsagePaths
{
    /// <summary>Set to a test folder to keep the database away from the real one.</summary>
    private static string? TestDir =>
        Environment.GetEnvironmentVariable("TOKEN_USAGE_DIR") is { Length: > 0 } dir ? dir : null;

    /// <summary>
    /// %USERPROFILE%\.token-usage, which also holds the installed exe. The database holds prompt labels,
    /// so it lives in the user's own profile, but outside AppData: when Claude Code is installed as an
    /// MSIX package, its hooks run inside the package and Windows redirects their AppData writes to the
    /// package's private copy, which the monitor (outside the package) never sees. Not under ~\.claude
    /// either, so it isn't tied to one tool's config folder.
    /// </summary>
    public static string DataDir => TestDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".token-usage");

    /// <summary>Where the database lived before it moved to <see cref="DataDir"/>.</summary>
    public static string LegacyDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenUsage");

    public static string DatabasePath => Path.Combine(DataDir, "token-usage.db");
    public static string ErrorLogPath => Path.Combine(DataDir, "token-usage-error.txt");

    /// <summary>Claude Code's session logs: %USERPROFILE%\.claude\projects, or under CLAUDE_CONFIG_DIR when set.</summary>
    public static string ClaudeProjectsDir => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "projects");

    /// <summary>
    /// Moves the database from <see cref="LegacyDataDir"/> to <see cref="DataDir"/> once, so the history
    /// is kept. Call before opening the database for writing. Skipped when TOKEN_USAGE_DIR is set.
    /// Run inside an MSIX package, the legacy path shows the package's redirected copy, so that data moves too.
    /// </summary>
    public static void MigrateLegacyData()
    {
        if (TestDir is null) MoveDatabase(LegacyDataDir, DataDir);
    }

    /// <summary>
    /// Moves token-usage.db with its WAL files (and the error log) from one folder to another, unless the
    /// target already has a database. Several hooks can start at once, so the move runs under a named
    /// mutex; the database file moves last, so a reader never finds it without its WAL.
    /// </summary>
    public static void MoveDatabase(string fromDir, string toDir)
    {
        var from = Path.Combine(fromDir, "token-usage.db");
        var to = Path.Combine(toDir, "token-usage.db");
        if (File.Exists(to) || !File.Exists(from)) return;

        using var mutex = new Mutex(false, "TokenUsage.MoveDatabase");
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { }   // a crashed process left it; the checks below still hold
        try
        {
            if (File.Exists(to) || !File.Exists(from)) return;
            Directory.CreateDirectory(toDir);
            foreach (var name in new[] { "token-usage.db-wal", "token-usage.db-shm", "token-usage-error.txt", "token-usage.db" })
            {
                var source = Path.Combine(fromDir, name);
                if (File.Exists(source)) File.Move(source, Path.Combine(toDir, name), overwrite: true);
            }
        }
        finally { mutex.ReleaseMutex(); }
    }
}
