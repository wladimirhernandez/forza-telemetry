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
/// <para>
/// It also knows whether the network those packets will arrive on is marked <b>Public</b> in Windows (<see cref="IsPublicNetwork"/>).
/// That is the single most common reason "everything is set up and nothing arrives": the installer's firewall rule only
/// covers Private networks. The lookup is asynchronous and best effort (see <see cref="INetworkProfileInfo"/>).
/// </para>
/// </remarks>
public sealed partial class DataOutViewModel : ObservableObject
{
    private readonly INetworkInfo? _networkInfo;
    private readonly IClipboardService? _clipboard;
    private readonly ILogger? _logger;
    private readonly INetworkProfileInfo? _networkProfile;

    // Incremented per lookup so a slow answer for an OLD address can never overwrite the answer for the current one.
    private int _categoryLookupVersion;

    /// <param name="port">The UDP port the game must send to.</param>
    /// <param name="networkInfo">Source of the LAN addresses; optional so tests that do not care can omit it.</param>
    /// <param name="clipboard">Target of the copy command; optional (the command then does nothing).</param>
    /// <param name="logger">Receives the address list on every read (so the log shows what the user was told); optional.</param>
    /// <param name="networkProfile">Tells whether the network of the primary address is Public; optional (no warning is ever shown without it).</param>
    public DataOutViewModel(
        int port,
        INetworkInfo? networkInfo = null,
        IClipboardService? clipboard = null,
        ILogger? logger = null,
        INetworkProfileInfo? networkProfile = null)
    {
        _port = port;
        _networkInfo = networkInfo;
        _clipboard = clipboard;
        _logger = logger;
        _networkProfile = networkProfile;
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

    /// <summary>
    /// True when Windows marks the network of <see cref="PrimaryAddress"/> as Public, so the guide shows how to switch it to
    /// Private. False while unknown (including before the first lookup finishes): a missing answer is never a warning.
    /// </summary>
    public bool IsPublicNetwork => Category == NetworkCategory.Public;

    /// <summary>
    /// The Windows network profile of <see cref="PrimaryAddress"/>, or <see cref="NetworkCategory.Unknown"/> while it is not known
    /// (no lookup yet, none possible, or it failed). The connection checklist reads this; the Settings warning reads
    /// <see cref="IsPublicNetwork"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPublicNetwork))]
    private NetworkCategory _category;

    /// <summary>True when there is an address to copy; enables the copy buttons.</summary>
    public bool HasAddress => PrimaryAddress is not null;

    /// <summary>Re-reads this PC's LAN addresses (synchronously; used once at startup).</summary>
    public void Refresh()
    {
        Apply(ReadAddresses(_networkInfo));
        _ = RefreshNetworkCategoryAsync(); // fire and forget by design: it never throws and must not delay startup
    }

    /// <summary>
    /// Adapter enumeration is a blocking OS call that can take a noticeable moment on a PC with many virtual adapters,
    /// so a Start click must not run it on the UI thread. Task.Run does the read on the thread pool; the await then
    /// resumes on the UI thread (WPF's synchronization context), where the bound properties may be set.
    /// </summary>
    public async Task RefreshAsync()
    {
        var network = _networkInfo;
        var addresses = network is null ? [] : await Task.Run(() => ReadAddresses(network));
        Apply(addresses);

        // Not awaited: the Start that called this must not wait for a COM lookup. The warning appears when it lands.
        _ = RefreshNetworkCategoryAsync();
    }

    /// <summary>
    /// Asks Windows whether the network of <see cref="PrimaryAddress"/> is Public and updates <see cref="IsPublicNetwork"/>.
    /// Never throws. Also what the "Check again" button runs, after the user switched the network to Private.
    /// </summary>
    [RelayCommand]
    public async Task RefreshNetworkCategoryAsync()
    {
        var provider = _networkProfile;
        var address = PrimaryAddress;
        var version = ++_categoryLookupVersion;

        var category = NetworkCategory.Unknown;
        if (provider is not null && address is not null && System.Net.IPAddress.TryParse(address, out var ip))
        {
            try
            {
                // The provider offloads its own blocking work; this await resumes on the UI thread.
                category = await provider.GetCategoryAsync(ip);
            }
            catch (Exception ex)
            {
                // A contract-breaking provider must still not break the Settings page or the Start button.
                if (_logger is not null)
                {
                    AppLog.NetworkProfileUnavailable(_logger, ex);
                }
            }

            if (_logger is not null && version == _categoryLookupVersion)
            {
                AppLog.NetworkProfile(_logger, address, category.ToString());
            }
        }

        // A newer lookup started while this one was waiting (the address changed): its answer is the one that counts.
        if (version == _categoryLookupVersion)
        {
            Category = category;
        }
    }

    /// <summary>
    /// Reads the adapters, treating ANY failure as "none found". The address is a convenience (the dashboard works without it),
    /// and this runs in the constructor at startup and inside every Start, so an adapter-enumeration failure must degrade to
    /// the "not found" text instead of ending the app or failing the Start.
    /// </summary>
    private IReadOnlyList<LanAddress> ReadAddresses(INetworkInfo? network)
    {
        try
        {
            return network?.GetLanAddresses() ?? [];
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                AppLog.LanAddressesUnavailable(_logger, ex);
            }

            return [];
        }
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
