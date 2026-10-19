using System.ComponentModel;
using System.Diagnostics;

namespace L2Mod.Cli.Leader;

/// <summary>Runs an external program (the GitHub CLI) on the same console; tests record the calls instead.</summary>
public interface ICommandRunner
{
    /// <returns>The program's exit code.</returns>
    int Run(string program, IReadOnlyList<string> arguments);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public int Run(string program, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{program} did not start.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"{program} could not be started ({exception.Message}). Install the GitHub CLI and run 'gh auth login'.", exception);
        }
    }
}
