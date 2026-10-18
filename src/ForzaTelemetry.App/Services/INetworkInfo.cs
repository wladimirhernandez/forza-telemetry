namespace ForzaTelemetry.App.Services;

/// <summary>Tells the view model which of this PC's addresses the Xbox can reach.</summary>
public interface INetworkInfo
{
    /// <summary>Best candidates first (the adapters most likely on the console's network). Empty when none is usable.</summary>
    IReadOnlyList<LanAddress> GetLanAddresses();
}
