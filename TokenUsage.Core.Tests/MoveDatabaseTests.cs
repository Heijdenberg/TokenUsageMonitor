using TokenUsage.Core;

namespace TokenUsage.Core.Tests;

public sealed class MoveDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "usage-move-" + Guid.NewGuid().ToString("N"));
    private readonly string _from;
    private readonly string _to;

    public MoveDatabaseTests()
    {
        _from = Path.Combine(_root, "old");
        _to = Path.Combine(_root, "new");
        Directory.CreateDirectory(_from);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void MovesDatabaseWithWalFilesAndErrorLog()
    {
        string[] names = ["token-usage.db", "token-usage.db-wal", "token-usage.db-shm", "token-usage-error.txt"];
        foreach (var name in names) File.WriteAllText(Path.Combine(_from, name), name);

        UsagePaths.MoveDatabase(_from, _to);

        foreach (var name in names)
        {
            Assert.False(File.Exists(Path.Combine(_from, name)));
            Assert.Equal(name, File.ReadAllText(Path.Combine(_to, name)));
        }
    }

    [Fact]
    public void LeavesBothAloneWhenTargetAlreadyHasDatabase()
    {
        File.WriteAllText(Path.Combine(_from, "token-usage.db"), "old");
        Directory.CreateDirectory(_to);
        File.WriteAllText(Path.Combine(_to, "token-usage.db"), "new");

        UsagePaths.MoveDatabase(_from, _to);

        Assert.Equal("old", File.ReadAllText(Path.Combine(_from, "token-usage.db")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_to, "token-usage.db")));
    }

    [Fact]
    public void DoesNothingWithoutOldDatabase()
    {
        UsagePaths.MoveDatabase(_from, _to);

        Assert.False(Directory.Exists(_to));
    }
}
