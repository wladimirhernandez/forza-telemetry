using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Recording;
using Microsoft.Extensions.Time.Testing;

namespace ForzaTelemetry.App.Tests;

/// <summary>Answers the delete question with a fixed answer and remembers what it was asked.</summary>
internal sealed class FakeConfirmation(bool answer) : IConfirmationService
{
    public List<(string Title, string Message, string Confirm, string Cancel)> Asked { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        Asked.Add((title, message, confirmText, cancelText));
        return Task.FromResult(answer);
    }
}

internal sealed class FakeFolderOpener : IFolderOpener
{
    public List<string> Opened { get; } = [];

    public bool TryOpen(string path)
    {
        Opened.Add(path);
        return true;
    }
}

internal sealed class MemorySettingsStore : ISettingsStore
{
    public AppSettings Load() => AppSettings.Default;

    public bool TrySave(AppSettings settings) => true;
}

/// <summary>
/// Everything the Sessions page touches, wired as the composition root wires it, on a temp folder: engines, a service whose live
/// source is a fake and whose replay source is the REAL replay of a recording file (at no-delay speed unless a test says
/// otherwise), a recorder, the library, the page and the shell.
/// </summary>
internal sealed class SessionsRig : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ForzaTelemetryTests", Guid.NewGuid().ToString("N"));

    public SessionsRig(
        bool confirmDelete = true, bool pauseReplayAtStart = false, double replaySpeed = ReplaySpeed.Max, bool withPerformance = false,
        CarNameStore? carNames = null, IFileSaveDialog? saveDialog = null)
    {
        Folder = Path.Combine(_root, "recordings");
        Engines = new TelemetryEngines();
        Recorder = new SessionRecorder(Clock);
        Library = new RecordingLibrary(Folder, Clock);
        Confirmation = new FakeConfirmation(confirmDelete);
        Opener = new FakeFolderOpener();
        Settings = new SettingsService(new MemorySettingsStore(), AppSettings.Default);

        Service = new TelemetryService(
            () =>
            {
                Source = new FakeTelemetrySource();
                return Source;
            },
            observers: Engines.Observers,
            replayFactory: path =>
            {
                var source = new ReplayTelemetrySource(path, Clock) { Speed = replaySpeed };
                if (pauseReplayAtStart)
                {
                    source.Pause();
                }

                return source;
            });

        if (withPerformance)
        {
            PerformanceHistory = new PerformanceHistoryStore(Path.Combine(_root, "performance.json"), Clock);
            Performance = new PerformanceViewModel(Engines.Acceleration, Engines.TopSpeed, Engines.CarChanges, PerformanceHistory);
        }

        Sessions = new SessionsViewModel(Service, Recorder, Library, Confirmation, Opener, carNames: carNames, saveDialog: saveDialog);
        Shell = new ShellViewModel(
            Service,
            Clock,
            dataOut: new DataOutViewModel(Settings.Current.UdpPort, null),
            settings: Settings,
            performancePage: Performance,
            engines: Engines,
            sessionsPage: Sessions);
    }

    public string Folder { get; }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 18, 1, 23, 45, TimeSpan.Zero));

    public TelemetryEngines Engines { get; }

    public SessionRecorder Recorder { get; }

    public RecordingLibrary Library { get; }

    public FakeConfirmation Confirmation { get; }

    public FakeFolderOpener Opener { get; }

    public SettingsService Settings { get; }

    public TelemetryService Service { get; }

    public SessionsViewModel Sessions { get; }

    public ShellViewModel Shell { get; }

    /// <summary>The Performance page and its store, present only when the rig was built <c>withPerformance</c>.</summary>
    public PerformanceViewModel? Performance { get; }

    public PerformanceHistoryStore? PerformanceHistory { get; }

    /// <summary>The live source built by the most recent live Start.</summary>
    public FakeTelemetrySource? Source { get; private set; }

    /// <summary>Writes a real recording into the library folder and returns its path.</summary>
    public Task<string> AddRecordingAsync(
        string name, int packets = 10, int car = 1234, DateTimeOffset? startedAt = null, int intervalMs = 100) =>
        RecordingFiles.WriteAsync(Folder, name, packets, intervalMs, car, startedAt);

    public async Task StartLiveAsync()
    {
        await Shell.StartCommand.ExecuteAsync(null);
        Assert.True(Service.IsRunning);
        Sessions.Refresh();
    }

    public async Task<IReadOnlyList<RecordingRowViewModel>> LoadLibraryAsync()
    {
        await Sessions.RefreshLibraryAsync();
        return Sessions.Rows;
    }

    public async ValueTask StopEverythingAsync()
    {
        await Sessions.PendingLibraryRefresh; // a rescan started by a stop may still be reading the files about to be deleted
        await Recorder.DisposeAsync();
        await Service.StopAsync();
    }

    public void Dispose()
    {
        StopEverythingAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
