namespace ForzaTelemetry.App.Services;

/// <summary>Shows a folder in Windows Explorer. An interface so a view model can offer "open the logs folder" and a test can check the path.</summary>
public interface IFolderOpener
{
    /// <summary>Opens <paramref name="path"/> (creating it first if it does not exist yet); false when Explorer could not be started.</summary>
    bool TryOpen(string path);
}
