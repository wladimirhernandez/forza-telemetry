using System.IO;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// The program and arguments that start another copy of this app, worked out from how THIS copy was started.
/// </summary>
/// <remarks>
/// <b>Why it is not just "run <c>Environment.ProcessPath</c>".</b> The process path is the executable of the running
/// process. For the installed app and <c>dotnet publish</c> output that is <c>ForzaTelemetry.App.exe</c>, and starting it
/// again is correct. But <c>dotnet run</c> and <c>dotnet ForzaTelemetry.App.dll</c> run the app INSIDE the generic
/// <c>dotnet</c> host: the process path is then <c>dotnet.exe</c>, and starting that alone just prints the .NET CLI help and
/// exits, so a "restart" would close the app and open nothing. In that case the app's own assembly must be passed to the
/// host as its first argument. The command-line arguments the user started the app with are forwarded in both cases, so a
/// relaunch behaves like the original launch.
/// <para>
/// Pure function of its inputs (no process or file access) so both shapes are unit-tested.
/// </para>
/// </remarks>
/// <param name="FileName">Executable to start.</param>
/// <param name="Arguments">Arguments, one element each (never joined into a string, so no quoting rules are involved).</param>
public sealed record RelaunchPlan(string FileName, IReadOnlyList<string> Arguments)
{
    /// <param name="processPath">The running process's executable (<c>Environment.ProcessPath</c>).</param>
    /// <param name="entryAssemblyPath">The app's own assembly file (<c>Assembly.GetEntryAssembly().Location</c>); only used under the dotnet host.</param>
    /// <param name="commandLineArguments">The arguments this copy was started with, WITHOUT the program name.</param>
    /// <returns>The plan, or null when the process path is unknown, or the dotnet host is in use but the assembly path is not.</returns>
    public static RelaunchPlan? Create(string? processPath, string? entryAssemblyPath, IReadOnlyList<string> commandLineArguments)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        if (!IsDotnetHost(processPath))
        {
            return new RelaunchPlan(processPath, commandLineArguments);
        }

        return string.IsNullOrEmpty(entryAssemblyPath)
            ? null
            : new RelaunchPlan(processPath, [entryAssemblyPath, .. commandLineArguments]);
    }

    /// <summary>True when the file is the generic <c>dotnet</c> host (<c>dotnet.exe</c> or <c>dotnet</c>), not the app's own executable.</summary>
    private static bool IsDotnetHost(string processPath) =>
        string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
}
