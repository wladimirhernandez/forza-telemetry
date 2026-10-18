using System.Net;

namespace ForzaTelemetry.App.Services;

/// <summary>The Windows "network profile" (firewall zone) of a network connection.</summary>
public enum NetworkCategory
{
    /// <summary>Could not be determined (no such adapter, the Windows service did not answer, ...). Never treated as a problem.</summary>
    Unknown = 0,

    /// <summary>Public: a café or hotel network. Windows applies its strictest firewall rules, which block incoming UDP.</summary>
    Public,

    /// <summary>Private: a home or trusted network. The installer's firewall rule applies here.</summary>
    Private,

    /// <summary>Domain: a corporate network joined to an Active Directory domain.</summary>
    Domain
}

/// <summary>
/// Tells which Windows network profile the connection that owns a given local address is in. An interface so the Data Out
/// guide can be tested with a fake, and so the real implementation (a Windows COM call) stays out of the view models.
/// </summary>
public interface INetworkProfileInfo
{
    /// <summary>
    /// The category of the network connection that owns <paramref name="localAddress"/>, or
    /// <see cref="NetworkCategory.Unknown"/> when it cannot be determined. Implementations must not block the calling
    /// thread (the UI thread calls this) and must not throw for an environmental failure.
    /// </summary>
    Task<NetworkCategory> GetCategoryAsync(IPAddress localAddress, CancellationToken cancellationToken = default);
}
