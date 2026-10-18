using ForzaTelemetry.App.Services;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App.Tests;

public class ExplorerFolderOpenerTests
{
    [Theory]
    [InlineData("C:\\logs\0bad")] // an embedded NUL: Directory.CreateDirectory throws ArgumentException, not IOException
    [InlineData("")] // an empty path: ArgumentException too
    public void Any_failure_is_logged_and_reported_as_false_never_thrown(string path)
    {
        var logger = new ListLogger<ExplorerFolderOpener>();

        var opened = new ExplorerFolderOpener(logger).TryOpen(path);

        Assert.False(opened);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Exception is not null);
    }
}
