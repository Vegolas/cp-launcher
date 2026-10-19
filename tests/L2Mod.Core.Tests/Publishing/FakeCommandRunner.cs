using L2Mod.Cli.Leader;

namespace L2Mod.Core.Tests.Publishing;

/// <summary>
/// Records the gh calls of publish. Keeps a copy of every release archive and its notes, because publish deletes
/// its scratch folder when it returns.
/// </summary>
public sealed class FakeCommandRunner : ICommandRunner
{
    private readonly string _captureDirectory;

    public FakeCommandRunner(string captureDirectory)
    {
        _captureDirectory = captureDirectory;
    }

    public List<string[]> Calls { get; } = new List<string[]>();

    public List<string> Archives { get; } = new List<string>();

    public List<string> ReleaseNotes { get; } = new List<string>();

    /// <summary>The exit code for a call; 0 (success) by default.</summary>
    public Func<string[], int> ExitCode { get; set; } = arguments => 0;

    public int Run(string program, IReadOnlyList<string> arguments)
    {
        Assert.Equal("gh", program);
        string[] call = arguments.ToArray();
        Calls.Add(call);
        if (call[0] == "release" && call[1] == "create")
        {
            Directory.CreateDirectory(_captureDirectory);
            string copy = Path.Combine(_captureDirectory, Path.GetFileName(call[3]));
            File.Copy(call[3], copy, true);
            Archives.Add(copy);
            ReleaseNotes.Add(File.ReadAllText(call[Array.IndexOf(call, "--notes-file") + 1]));
        }

        return ExitCode(call);
    }
}
