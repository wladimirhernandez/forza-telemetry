using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App.Services;

/// <summary>Real <see cref="IFolderOpener"/>: asks the Windows shell to open the folder in Explorer.</summary>
public sealed class ExplorerFolderOpener(ILogger logger) : IFolderOpener
{
    public bool TryOpen(string path)
    {
        try
        {
            // The folder may not exist yet (nothing logged, or logging could not start): Explorer would show an error
            // dialog for a missing path, so create it first.
            Directory.CreateDirectory(path);

            // UseShellExecute = true hands the path to the Windows shell, which opens it in the user's file manager.
            // (Without it .NET would try to run the path as a program.)
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            AppLog.OpenFolderFailed(logger, ex, path);
            return false;
        }
    }
}
