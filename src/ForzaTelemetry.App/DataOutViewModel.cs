using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;
using Microsoft.Extensions.Logging;

namespace ForzaTelemetry.App;

/// <summary>
/// "Where to point the game": this PC's LAN address(es) and the UDP port, plus a command that copies the address.
/// </summary>
/// <remarks>
/// A view model of its own (instead of properties on the shell) because TWO screens show it: the top bar pill and the
/// Data Out guide on the Settings page. Both bind to the same instance, so they can never disagree.
/// </remarks>
public sealed partial class DataOutViewModel : ObservableObject
{
    private readonly INetworkInfo? _networkInfo;
    private readonly IClipboardService? _clipboard;
    private readonly ILogger? _logger;

    /// <param name="port">The UDP port the game must send to.</param>
    /// <param name="networkInfo">Source of the LAN addresses; optional so tests that do not care can omit it.</param>
    /// <param name="clipboard">Target of the copy command; optional (the command then does nothing).</param>
    /// <param name="logger">Receives the address list on every read (so the log shows what the user was told); optional.</param>
    public DataOutViewModel(
        int port, INetworkInfo? networkInfo = null, IClipboardService? clipboard = null, ILogger? logger = null)
    {
        _port = port;
        _networkInfo = networkInfo;
        _clipboard = clipboard;
        _logger = logger;
        Refresh(); // the one read at startup
    }

    /// <summary>
    /// What to type into the game's Data Out IP field: this PC's likely LAN address(es), comma-separated, or a
    /// localized "not found" text. Refreshed on every Start, since the network can change while the app is open.
    /// </summary>
    [ObservableProperty]
    private string _address = Strings.DataOutIpNone;

    /// <summary>The best-ranked LAN address alone (what the copy button copies), or null when none was found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddress))]
    [NotifyCanExecuteChangedFor(nameof(CopyAddressCommand))]
    private string? _primaryAddress;

    /// <summary>The UDP port the game must send to.</summary>
    [ObservableProperty]
    private int _port;

    /// <summary>True when there is an address to copy; enables the copy buttons.</summary>
    public bool HasAddress => PrimaryAddress is not null;

    /// <summary>Re-reads this PC's LAN addresses (synchronously; used once at startup).</summary>
    public void Refresh() => Apply(_networkInfo?.GetLanAddresses() ?? []);

    /// <summary>
    /// Adapter enumeration is a blocking OS call that can take a noticeable moment on a PC with many virtual adapters,
    /// so a Start click must not run it on the UI thread. Task.Run does the read on the thread pool; the await then
    /// resumes on the UI thread (WPF's synchronization context), where the bound properties may be set.
    /// </summary>
    public async Task RefreshAsync()
    {
        var network = _networkInfo;
        var addresses = network is null ? [] : await Task.Run(network.GetLanAddresses);
        Apply(addresses);
    }

    private void Apply(IReadOnlyList<LanAddress> addresses)
    {
        Address = addresses.Count == 0
            ? Strings.DataOutIpNone
            : string.Join(", ", addresses.Select(a => a.Address.ToString()));

        // What the copy button puts on the clipboard: ONE address (the best-ranked), not the comma-separated list.
        PrimaryAddress = addresses.Count == 0 ? null : addresses[0].Address.ToString();

        if (_logger is not null)
        {
            AppLog.LanAddresses(
                _logger,
                addresses.Count == 0 ? "(none found)" : string.Join(", ", addresses.Select(a => $"{a.Address} ({a.AdapterName})")));
        }
    }

    // CanExecute = HasAddress: with nothing to copy the bound button disables itself, no extra view logic needed.
    [RelayCommand(CanExecute = nameof(HasAddress))]
    private void CopyAddress()
    {
        if (PrimaryAddress is { } address)
        {
            _clipboard?.TrySetText(address);
        }
    }
}
