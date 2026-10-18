using System.Globalization;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App;

/// <summary>What the "Connect your game" guide asks of the window around it: start listening, and leave for the dashboard.</summary>
/// <remarks>Same idea as <see cref="ISessionsHost"/>: the guide never references the shell, the shell attaches itself.</remarks>
public interface IConnectionGuideHost
{
    /// <summary>Starts the live listener through the same serialized path as the top bar's Start button.</summary>
    Task StartListeningAsync();

    /// <summary>Switches to the Dashboard tab.</summary>
    void ShowDashboard();
}

/// <summary>
/// The first-run "Connect your game" guide: where to point the game, and a live checklist that turns "nothing happens" into the
/// one thing still missing.
/// </summary>
/// <remarks>
/// It owns no clock of its own and no network code: the shell calls <see cref="Refresh"/> on its 20 Hz tick, and each call reads
/// the telemetry service, the sender tracker and the network profile and hands them to <see cref="ConnectionChecklist"/>, the pure
/// function that decides every line. That keeps this class a thin mapper from decisions to sentences, and the decisions testable.
/// <para>
/// <b>When it opens.</b> By itself once, on the first launch (<see cref="OpenIfFirstRun"/>, until the user finishes or dismisses it,
/// which persists <see cref="AppSettings.HasCompletedSetup"/>), and on request afterwards from the dashboard's waiting state and from
/// Settings. While it is closed, <see cref="Refresh"/> does nothing, so it costs the tick nothing.
/// </para>
/// </remarks>
public sealed partial class ConnectionGuideViewModel : ObservableObject
{
    private readonly TelemetryService _service;
    private readonly IDataOutSenderInfo? _sender;
    private readonly TimeProvider _time;
    private readonly SettingsService? _settings;
    private IConnectionGuideHost? _host;

    // When the current LIVE listening started (null while not listening), to time the "nothing arrives" hint.
    private DateTimeOffset? _listeningSince;

    // "Seen driving" is sticky for the listening session: a pause menu or a loading screen sends IsRaceOn = false and must not
    // take the tick away again. Cleared when listening stops or the guide is opened afresh.
    private bool _drivingSeen;

    /// <param name="service">Read for the running/fault state and the latest packet.</param>
    /// <param name="dataOut">The address, port and network profile the guide shows.</param>
    /// <param name="sender">Who the datagrams come from; optional (the line then shows no address).</param>
    /// <param name="time">The clock for the "nothing arrives" delay; injectable so a test does not wait 20 seconds.</param>
    /// <param name="settings">Where "setup completed" is saved; optional (nothing is persisted then).</param>
    public ConnectionGuideViewModel(
        TelemetryService service,
        DataOutViewModel dataOut,
        IDataOutSenderInfo? sender = null,
        TimeProvider? time = null,
        SettingsService? settings = null)
    {
        _service = service;
        DataOut = dataOut;
        _sender = sender;
        _time = time ?? TimeProvider.System;
        _settings = settings;

        ListeningStep = new ChecklistStepViewModel(ListeningTitle());
        NetworkStep = new ChecklistStepViewModel(SetupStrings.SetupCheckNetwork);
        PacketsStep = new ChecklistStepViewModel(SetupStrings.SetupCheckPackets);
        DrivingStep = new ChecklistStepViewModel(SetupStrings.SetupCheckDriving);
        Steps = [ListeningStep, NetworkStep, PacketsStep, DrivingStep];
        Apply(ConnectionChecklist.Evaluate(default), default);

        // The first line names the port, which can change while the app runs.
        dataOut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DataOutViewModel.Port))
            {
                ListeningStep.Title = ListeningTitle();
            }
        };
    }

    /// <summary>The address and port shown big at the top, with the copy command.</summary>
    public DataOutViewModel DataOut { get; }

    /// <summary>What to type when the game runs on this same PC.</summary>
    public string LocalAddress => "127.0.0.1";

    /// <summary>The checklist lines in order.</summary>
    public IReadOnlyList<ChecklistStepViewModel> Steps { get; }

    /// <summary>Line 1.</summary>
    public ChecklistStepViewModel ListeningStep { get; }

    /// <summary>Line 2.</summary>
    public ChecklistStepViewModel NetworkStep { get; }

    /// <summary>Line 3.</summary>
    public ChecklistStepViewModel PacketsStep { get; }

    /// <summary>Line 4.</summary>
    public ChecklistStepViewModel DrivingStep { get; }

    /// <summary>The guide is on screen (a modal card over the window).</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Live driving data is flowing: the card switches to "You're connected".</summary>
    [ObservableProperty]
    private bool _isConnected;

    /// <summary>Listening a while with nothing arriving: the list of usual causes is shown.</summary>
    [ObservableProperty]
    private bool _showTroubleshooting;

    /// <summary>The network is Public and nothing has arrived: the steps to switch it to Private are shown.</summary>
    [ObservableProperty]
    private bool _showNetworkFix;

    /// <summary>The listener is not running, so the "Start listening" button makes sense.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartListeningCommand))]
    private bool _canStartListening = true;

    /// <summary>Called by the shell when it is built; the guide cannot start listening or navigate without it.</summary>
    public void Attach(IConnectionGuideHost host) => _host = host;

    /// <summary>Opens the guide with a clean slate (nothing is "already seen"), and refreshes the network profile once.</summary>
    [RelayCommand]
    public void Open()
    {
        _drivingSeen = false;
        IsOpen = true;
        Refresh();
        DataOut.RefreshNetworkCategoryCommand.Execute(null); // the user may have changed the profile since startup
    }

    /// <summary>Opens the guide only when the user never finished or dismissed it (the first launch).</summary>
    public void OpenIfFirstRun()
    {
        if (_settings is { Current.HasCompletedSetup: false })
        {
            Open();
        }
    }

    /// <summary>Closes the guide and remembers that setup was seen, so it does not open by itself again.</summary>
    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        MarkSetupCompleted();
    }

    /// <summary>Closes the guide and goes to the dashboard (the primary action once connected).</summary>
    [RelayCommand]
    private void GoToDashboard()
    {
        Close();
        _host?.ShowDashboard();
    }

    [RelayCommand(CanExecute = nameof(CanStartListening))]
    private async Task StartListening()
    {
        if (_host is not null)
        {
            await _host.StartListeningAsync();
        }
    }

    /// <summary>
    /// Re-reads the live state and updates the checklist. Called on every tick of the shell; does nothing while the guide is closed.
    /// </summary>
    public void Refresh()
    {
        if (!IsOpen)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var live = _service.IsRunning && _service.Mode == TelemetrySourceMode.Live;
        if (live)
        {
            _listeningSince ??= now;
        }
        else
        {
            _listeningSince = null;
            _drivingSeen = false;
        }

        var latest = _service.Latest;
        if (live && latest is { IsRaceOn: true })
        {
            _drivingSeen = true;
        }

        var sender = live ? _sender?.LastSenderAddress : null;
        var signals = new ConnectionSignals(
            IsListening: _service.IsRunning,
            IsReplaying: _service.IsReplaying,
            HasFault: _service.Fault is not null,
            Network: DataOut.Category,
            HasSender: sender is not null,
            HasPackets: live && latest is not null,
            HasDriven: _drivingSeen,
            ListeningFor: live && _listeningSince is { } since ? now - since : TimeSpan.Zero);

        Apply(ConnectionChecklist.Evaluate(signals), signals, sender);
        CanStartListening = !_service.IsRunning;

        if (IsConnected)
        {
            MarkSetupCompleted(); // connected is as complete as setup gets, even if the window is closed by quitting
        }
    }

    private void Apply(ChecklistResult result, ConnectionSignals signals, string? sender = null)
    {
        ListeningStep.State = result.Listening;
        ListeningStep.Detail = ListeningDetail(result.Listening, signals);

        NetworkStep.State = result.Network;
        NetworkStep.Detail = NetworkDetail(result.Network, signals.Network);

        PacketsStep.State = result.Packets;
        PacketsStep.Detail = result.Packets switch
        {
            CheckState.Ok => sender is null
                ? SetupStrings.SetupPacketsOkNoSender
                : string.Format(CultureInfo.CurrentCulture, SetupStrings.SetupPacketsOk, sender),
            CheckState.Warning => string.Format(
                CultureInfo.CurrentCulture, SetupStrings.SetupPacketsWarning, (int)signals.ListeningFor.TotalSeconds),
            _ => SetupStrings.SetupPacketsPending
        };

        DrivingStep.State = result.Driving;
        DrivingStep.Detail = result.Driving == CheckState.Ok ? SetupStrings.SetupDrivingOk : SetupStrings.SetupDrivingPending;

        IsConnected = result.IsConnected;
        ShowTroubleshooting = result.ShowTroubleshooting;
        ShowNetworkFix = result.Network == CheckState.Warning;
    }

    private string ListeningTitle() =>
        string.Format(CultureInfo.CurrentCulture, SetupStrings.SetupCheckListening, DataOut.Port);

    private string ListeningDetail(CheckState state, ConnectionSignals signals)
    {
        if (signals.HasFault && _service.Fault is { } fault)
        {
            return fault is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                ? string.Format(CultureInfo.CurrentCulture, Strings.ErrorPortInUse, DataOut.Port)
                : string.Format(CultureInfo.CurrentCulture, Strings.ErrorGeneric, fault.Message);
        }

        return state == CheckState.Ok
            ? SetupStrings.SetupListeningOk
            : signals.IsReplaying ? SetupStrings.SetupListeningReplay : SetupStrings.SetupListeningPending;
    }

    private static string NetworkDetail(CheckState state, NetworkCategory category) => category switch
    {
        NetworkCategory.Private => SetupStrings.SetupNetworkPrivate,
        NetworkCategory.Domain => SetupStrings.SetupNetworkDomain,
        NetworkCategory.Public => state == CheckState.Ok ? SetupStrings.SetupNetworkPublicWorks : SetupStrings.SetupNetworkPublic,
        _ => SetupStrings.SetupNetworkUnknown
    };

    private void MarkSetupCompleted()
    {
        if (_settings is { Current.HasCompletedSetup: false })
        {
            _settings.Update(s => s with { HasCompletedSetup = true });
        }
    }
}
