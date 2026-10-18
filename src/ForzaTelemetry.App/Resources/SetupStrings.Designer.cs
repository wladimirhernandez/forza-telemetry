//------------------------------------------------------------------------------
// Hand-maintained strongly typed accessor for SetupStrings.resx (same shape as SessionsStrings).
// It is a separate resource set so the connection guide does not touch Strings.resx. Add a property
// here whenever a key is added to SetupStrings.resx (and to SetupStrings.es.resx).
//------------------------------------------------------------------------------

#nullable enable

namespace ForzaTelemetry.App.Resources;

/// <summary>SetupStrings (neutral English; Spanish in SetupStrings.es.resx).</summary>
public static class SetupStrings
{
    private static global::System.Resources.ResourceManager? _resourceManager;

    /// <summary>The resource manager for <c>SetupStrings.resx</c> and its satellite cultures.</summary>
    public static global::System.Resources.ResourceManager ResourceManager =>
        _resourceManager ??= new global::System.Resources.ResourceManager(
            "ForzaTelemetry.App.Resources.SetupStrings", typeof(SetupStrings).Assembly);

    /// <summary>Culture override for lookups; null uses the current UI culture (as set by LanguageService).</summary>
    public static global::System.Globalization.CultureInfo? Culture { get; set; }

    /// <summary>"Connect your game"</summary>
    public static string SetupTitle => ResourceManager.GetString("SetupTitle", Culture)!;

    /// <summary>"Point Forza Horizon at this PC. The checklist updates live as soon as the game sends data."</summary>
    public static string SetupSubtitle => ResourceManager.GetString("SetupSubtitle", Culture)!;

    /// <summary>"Data Out IP address"</summary>
    public static string SetupAddressLabel => ResourceManager.GetString("SetupAddressLabel", Culture)!;

    /// <summary>"Port"</summary>
    public static string SetupPortLabel => ResourceManager.GetString("SetupPortLabel", Culture)!;

    /// <summary>"Copy IP"</summary>
    public static string SetupCopyButton => ResourceManager.GetString("SetupCopyButton", Culture)!;

    /// <summary>"Which IP do I use?"</summary>
    public static string SetupWhichIpTitle => ResourceManager.GetString("SetupWhichIpTitle", Culture)!;

    /// <summary>"The game runs on this PC"</summary>
    public static string SetupSamePcTitle => ResourceManager.GetString("SetupSamePcTitle", Culture)!;

    /// <summary>"Type 127.0.0.1 as the IP address."</summary>
    public static string SetupSamePcBody => ResourceManager.GetString("SetupSamePcBody", Culture)!;

    /// <summary>"Xbox or another PC"</summary>
    public static string SetupOtherDeviceTitle => ResourceManager.GetString("SetupOtherDeviceTitle", Culture)!;

    /// <summary>"Type the address shown above. Both devices must be on the same network."</summary>
    public static string SetupOtherDeviceBody => ResourceManager.GetString("SetupOtherDeviceBody", Culture)!;

    /// <summary>"In the game"</summary>
    public static string SetupInGameTitle => ResourceManager.GetString("SetupInGameTitle", Culture)!;

    /// <summary>"Open Settings, then HUD and Gameplay."</summary>
    public static string SetupInGameStep1 => ResourceManager.GetString("SetupInGameStep1", Culture)!;

    /// <summary>"Turn Data Out on."</summary>
    public static string SetupInGameStep2 => ResourceManager.GetString("SetupInGameStep2", Culture)!;

    /// <summary>"Enter the IP address and the port from the left."</summary>
    public static string SetupInGameStep3 => ResourceManager.GetString("SetupInGameStep3", Culture)!;

    /// <summary>"Live checklist"</summary>
    public static string SetupChecklistTitle => ResourceManager.GetString("SetupChecklistTitle", Culture)!;

    /// <summary>"Listening on UDP port {0}"</summary>
    public static string SetupCheckListening => ResourceManager.GetString("SetupCheckListening", Culture)!;

    /// <summary>"Network profile is Private"</summary>
    public static string SetupCheckNetwork => ResourceManager.GetString("SetupCheckNetwork", Culture)!;

    /// <summary>"Packets arriving"</summary>
    public static string SetupCheckPackets => ResourceManager.GetString("SetupCheckPackets", Culture)!;

    /// <summary>"Driving"</summary>
    public static string SetupCheckDriving => ResourceManager.GetString("SetupCheckDriving", Culture)!;

    /// <summary>"done"</summary>
    public static string SetupStateOk => ResourceManager.GetString("SetupStateOk", Culture)!;

    /// <summary>"waiting"</summary>
    public static string SetupStatePending => ResourceManager.GetString("SetupStatePending", Culture)!;

    /// <summary>"needs attention"</summary>
    public static string SetupStateWarning => ResourceManager.GetString("SetupStateWarning", Culture)!;

    /// <summary>"The app is not listening yet."</summary>
    public static string SetupListeningPending => ResourceManager.GetString("SetupListeningPending", Culture)!;

    /// <summary>"Ready to receive data from the game."</summary>
    public static string SetupListeningOk => ResourceManager.GetString("SetupListeningOk", Culture)!;

    /// <summary>"A recording is playing, not the game. Start listening to stop the replay and wait for the game."</summary>
    public static string SetupListeningReplay => ResourceManager.GetString("SetupListeningReplay", Culture)!;

    /// <summary>"Start listening"</summary>
    public static string SetupStartListeningButton => ResourceManager.GetString("SetupStartListeningButton", Culture)!;

    /// <summary>"Stop replay and start listening"</summary>
    public static string SetupStartListeningReplayButton => ResourceManager.GetString("SetupStartListeningReplayButton", Culture)!;

    /// <summary>"Windows treats this network as Private, so incoming game data is allowed."</summary>
    public static string SetupNetworkPrivate => ResourceManager.GetString("SetupNetworkPrivate", Culture)!;

    /// <summary>"Windows treats this network as a Domain network."</summary>
    public static string SetupNetworkDomain => ResourceManager.GetString("SetupNetworkDomain", Culture)!;

    /// <summary>"Public networks block incoming game data. Switch it to Private (steps below)."</summary>
    public static string SetupNetworkPublic => ResourceManager.GetString("SetupNetworkPublic", Culture)!;

    /// <summary>"This network is Public, but data is getting through anyway."</summary>
    public static string SetupNetworkPublicWorks => ResourceManager.GetString("SetupNetworkPublicWorks", Culture)!;

    /// <summary>"Could not be checked. It only matters if no packets arrive."</summary>
    public static string SetupNetworkUnknown => ResourceManager.GetString("SetupNetworkUnknown", Culture)!;

    /// <summary>"No data received yet."</summary>
    public static string SetupPacketsPending => ResourceManager.GetString("SetupPacketsPending", Culture)!;

    /// <summary>"Receiving data from {0}."</summary>
    public static string SetupPacketsOk => ResourceManager.GetString("SetupPacketsOk", Culture)!;

    /// <summary>"Receiving data."</summary>
    public static string SetupPacketsOkNoSender => ResourceManager.GetString("SetupPacketsOkNoSender", Culture)!;

    /// <summary>"Nothing has arrived after {0} seconds. Check the list below."</summary>
    public static string SetupPacketsWarning => ResourceManager.GetString("SetupPacketsWarning", Culture)!;

    /// <summary>"Waiting for you to drive. Menus and the pause screen do not send driving data."</summary>
    public static string SetupDrivingPending => ResourceManager.GetString("SetupDrivingPending", Culture)!;

    /// <summary>"Driving data is flowing."</summary>
    public static string SetupDrivingOk => ResourceManager.GetString("SetupDrivingOk", Culture)!;

    /// <summary>"Switch this network to Private"</summary>
    public static string SetupNetworkFixTitle => ResourceManager.GetString("SetupNetworkFixTitle", Culture)!;

    /// <summary>"Nothing arriving? Check these"</summary>
    public static string SetupTroubleTitle => ResourceManager.GetString("SetupTroubleTitle", Culture)!;

    /// <summary>"The IP in the game is exactly the one shown here, or 127.0.0.1 when the game runs on this PC."</summary>
    public static string SetupTroubleIp => ResourceManager.GetString("SetupTroubleIp", Culture)!;

    /// <summary>"Windows Firewall allows incoming UDP on this port. The installer adds the rule for Private networks."</summary>
    public static string SetupTroubleFirewall => ResourceManager.GetString("SetupTroubleFirewall", Culture)!;

    /// <summary>"A VPN can block local network access. Pause it or allow LAN access."</summary>
    public static string SetupTroubleVpn => ResourceManager.GetString("SetupTroubleVpn", Culture)!;

    /// <summary>"The console and this PC are on the same network and subnet (not a guest Wi-Fi, not another router)."</summary>
    public static string SetupTroubleSubnet => ResourceManager.GetString("SetupTroubleSubnet", Culture)!;

    /// <summary>"The game is in a drive: Data Out sends nothing from menus and loading screens."</summary>
    public static string SetupTroubleDriving => ResourceManager.GetString("SetupTroubleDriving", Culture)!;

    /// <summary>"You're connected"</summary>
    public static string SetupConnectedTitle => ResourceManager.GetString("SetupConnectedTitle", Culture)!;

    /// <summary>"Live driving data is arriving from the game."</summary>
    public static string SetupConnectedBody => ResourceManager.GetString("SetupConnectedBody", Culture)!;

    /// <summary>"Go to dashboard"</summary>
    public static string SetupGoToDashboard => ResourceManager.GetString("SetupGoToDashboard", Culture)!;

    /// <summary>"Close"</summary>
    public static string SetupCloseButton => ResourceManager.GetString("SetupCloseButton", Culture)!;

    /// <summary>"Open setup guide"</summary>
    public static string SetupOpenGuideButton => ResourceManager.GetString("SetupOpenGuideButton", Culture)!;

    /// <summary>"How to connect"</summary>
    public static string SetupHowToConnect => ResourceManager.GetString("SetupHowToConnect", Culture)!;

    /// <summary>"Not receiving data from the game yet."</summary>
    public static string SetupDashboardHint => ResourceManager.GetString("SetupDashboardHint", Culture)!;

    /// <summary>"A step-by-step guide with a live checklist of what is working."</summary>
    public static string SetupGuideHint => ResourceManager.GetString("SetupGuideHint", Culture)!;
}
