using ForzaTelemetry.App.Services;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App.Tests;

public class AppLoggingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Default_directory_is_under_LocalAppData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, AppLogging.DefaultLogDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("ForzaTelemetry", "logs"), AppLogging.DefaultLogDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Writes_a_daily_log_file_that_is_flushed_on_dispose()
    {
        var logging = AppLogging.Create(_directory);
        logging.Factory.CreateLogger("test").LogInformation("hello {Name}", "world");
        logging.Dispose();

        var file = Assert.Single(Directory.GetFiles(_directory, "telemetry-*.log"));
        Assert.Contains("hello world", ReadShared(file));
        Assert.Equal(_directory, logging.LogDirectory);
    }

    [Fact]
    public void Falls_back_to_a_silent_logger_when_the_directory_cannot_be_created()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "file-not-folder");
        File.WriteAllText(blocker, "x");

        // A path UNDER a regular file can never become a directory.
        using var logging = AppLogging.Create(Path.Combine(blocker, "logs"));

        Assert.Null(logging.LogDirectory);
        logging.Factory.CreateLogger("test").LogError("must not throw");
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
