using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForzaTelemetry.App.Services;

/// <summary>
/// Real <see cref="INetworkProfileInfo"/>: asks the Windows Network List Manager (NLM) which profile (Public, Private,
/// Domain) the connection of an address has.
/// </summary>
/// <remarks>
/// <b>Why NLM and not the other options.</b> Three ways to read the profile exist: the <c>MSFT_NetConnectionProfile</c> CIM
/// class (what PowerShell's <c>Get-NetConnectionProfile</c> uses), the <c>NetworkListManager</c> COM API, and parsing
/// <c>netsh</c> output. NLM is the source of truth the other two read from, so there is no extra layer to be disabled or to
/// disagree. CIM would need the <c>System.Management</c> NuGet package (a new dependency, and the WMI service in the
/// middle) and netsh output is localized text. NLM needs no package, and its adapter id is the same GUID that
/// <see cref="NetworkInterface.Id"/> reports, so "which connection owns 192.168.0.204" is an exact match, not a guess by name.
/// <para>
/// <b>Why the work runs on the thread pool.</b> COM activation of the service and the enumeration can take tens of
/// milliseconds (more when the service is cold). <see cref="Task.Run(Func{NetworkCategory})"/> keeps that off the UI thread;
/// the thread-pool thread is in the multithreaded COM apartment, which NLM supports.
/// </para>
/// <para>
/// Everything is best effort. Any failure becomes <see cref="NetworkCategory.Unknown"/> and a log line, because a missing
/// hint must never cost the user anything: the guide just shows no warning.
/// </para>
/// </remarks>
public sealed class NetworkListManagerProfileInfo(ILogger? logger = null) : INetworkProfileInfo
{
    // CLSID of the NetworkListManager coclass (netlistmgr.h: CLSID_NetworkListManager).
    private static readonly Guid NetworkListManagerClass = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public Task<NetworkCategory> GetCategoryAsync(IPAddress localAddress, CancellationToken cancellationToken = default) =>
        Task.Run(() => Query(localAddress), cancellationToken);

    private NetworkCategory Query(IPAddress localAddress)
    {
        try
        {
            return FindAdapterId(localAddress) is { } adapterId ? QueryCategory(adapterId) : NetworkCategory.Unknown;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException
            or NetworkInformationException or PlatformNotSupportedException)
        {
            AppLog.NetworkProfileUnavailable(_logger, ex);
            return NetworkCategory.Unknown;
        }
    }

    /// <summary>The adapter GUID of the interface that has <paramref name="address"/>, or null when none does.</summary>
    private static Guid? FindAdapterId(IPAddress address)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(address)) && Guid.TryParse(nic.Id, out var id))
            {
                return id;
            }
        }

        return null;
    }

    private static NetworkCategory QueryCategory(Guid adapterId)
    {
        var type = Type.GetTypeFromCLSID(NetworkListManagerClass)
            ?? throw new InvalidOperationException("The Network List Manager is not registered.");
        var manager = (INetworkListManager)Activator.CreateInstance(type)!;
        var connections = manager.GetNetworkConnections();
        try
        {
            // The enumerator hands out one connection per call; Next answers S_FALSE (1) when the list is exhausted.
            while (connections.Next(1, out var connection, out var fetched) == 0 && fetched == 1)
            {
                try
                {
                    if (connection.GetAdapterId() == adapterId)
                    {
                        var network = connection.GetNetwork();
                        try
                        {
                            return FromNlm(network.GetCategory());
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(network);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(connection);
                }
            }

            return NetworkCategory.Unknown;
        }
        finally
        {
            Marshal.ReleaseComObject(connections);
            Marshal.ReleaseComObject(manager);
        }
    }

    /// <summary>Maps NLM_NETWORK_CATEGORY (0 public, 1 private, 2 domain-authenticated) to <see cref="NetworkCategory"/>.</summary>
    public static NetworkCategory FromNlm(int nlmCategory) => nlmCategory switch
    {
        0 => NetworkCategory.Public,
        1 => NetworkCategory.Private,
        2 => NetworkCategory.Domain,
        _ => NetworkCategory.Unknown
    };

    // ---- COM interop ------------------------------------------------------------------------------------------------
    //
    // Hand-written minimal declarations of the four NLM interfaces (netlistmgr.h). A COM interface is a table of function
    // pointers: the CLR calls the Nth method by position, so each interface lists its methods in the header's EXACT order,
    // and may stop after the last one used (trailing methods are never called). They are all "dual" interfaces, which means
    // the first seven slots are IDispatch's; InterfaceIsDual tells the CLR to account for them. Where the IDL marks a
    // parameter [out, retval], the CLR turns the HRESULT-returning method into one that returns the value.

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworks(int flags);

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetwork(Guid networkId);

        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object get__NewEnum();

        // PreserveSig: Next answers S_FALSE (1) at the end of the list, which is not an error, so the raw HRESULT is wanted.
        [PreserveSig]
        int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out INetworkConnection connection, out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();

        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool get_IsConnectedToInternet();

        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool get_IsConnected();

        int GetConnectivity();

        Guid GetConnectionId();

        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetName();

        void SetName([MarshalAs(UnmanagedType.BStr)] string name);

        [return: MarshalAs(UnmanagedType.BStr)]
        string GetDescription();

        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);

        Guid GetNetworkId();

        int GetDomainType();

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworkConnections();

        void GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);

        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool get_IsConnectedToInternet();

        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool get_IsConnected();

        int GetConnectivity();

        int GetCategory();
    }
}
