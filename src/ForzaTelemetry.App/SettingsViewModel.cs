using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App;

/// <summary>
/// The Settings screen: what the user can edit, and what happens when they do.
/// </summary>
/// <remarks>
/// Every control writes straight to <see cref="SettingsService"/> the moment it changes (units, language, auto-start) and
/// the service saves and notifies. The one exception is the UDP port: applying it RESTARTS the listener, so it waits for an
/// explicit Apply (button or Enter) instead of firing on every keystroke (typing "5301" would otherwise rebind the socket on
/// "5", "53", "530" and "5301").
/// <para>
/// Validation is a plain property (<see cref="PortError"/>) the view shows next to the box. WPF also has a built-in
/// validation system (<c>IDataErrorInfo</c>, <c>ValidationRules</c>); this screen has one rule and one message, so a
/// computed property is far less machinery and is trivially unit-testable.
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IFolderOpener? _folderOpener;
    private readonly IAppRestarter? _restarter;
    private readonly ConnectionGuideViewModel? _guide;

    // The language that is actually on screen right now: it was applied at startup, so a different SELECTED language only
    // takes effect after a restart (see LanguageChangePending).
    private readonly AppLanguage _activeLanguage;

    // What AppLanguage.System resolves to on this PC; injectable so a test does not depend on the machine's language.
    private readonly CultureInfo? _systemCulture;

    /// <param name="settings">The live settings this screen edits.</param>
    /// <param name="dataOut">The same address/port object the top bar shows.</param>
    /// <param name="logDirectory">Folder the "Open logs folder" button opens.</param>
    /// <param name="folderOpener">Opens the folder; optional (the button then does nothing).</param>
    /// <param name="restarter">Restarts the app for a language change; optional (the restart button is then hidden).</param>
    /// <param name="systemCulture">The culture "System" means; optional (the one Windows gave the process at startup).</param>
    /// <param name="guide">The "Connect your game" guide the Connection card opens; optional (the button then does nothing).</param>
    public SettingsViewModel(
        SettingsService settings,
        DataOutViewModel dataOut,
        string logDirectory,
        IFolderOpener? folderOpener = null,
        IAppRestarter? restarter = null,
        CultureInfo? systemCulture = null,
        ConnectionGuideViewModel? guide = null)
    {
        _settings = settings;
        _guide = guide;
        DataOut = dataOut;
        LogDirectory = logDirectory;
        _folderOpener = folderOpener;
        _restarter = restarter;
        _systemCulture = systemCulture;

        var current = settings.Current;
        _portText = current.UdpPort.ToString(CultureInfo.InvariantCulture);
        _speedUnit = current.SpeedUnit;
        _temperatureUnit = current.TemperatureUnit;
        _language = current.Language;
        _startListeningOnLaunch = current.StartListeningOnLaunch;
        _activeLanguage = current.Language;

        // The Data Out guide quotes the live address and port, so it must re-render when either changes.
        dataOut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DataOutViewModel.Address) or nameof(DataOutViewModel.Port))
            {
                OnPropertyChanged(nameof(GuideEndpointStep));
                OnPropertyChanged(nameof(FirewallNote));
            }
        };

        // The listener can be restarted on a new port from elsewhere (only this screen changes it today, but the setting is
        // shared): keep the box in step with the stored value.
        settings.Changed += (_, e) =>
        {
            if (e.Current.UdpPort != e.Previous.UdpPort)
            {
                PortText = e.Current.UdpPort.ToString(CultureInfo.InvariantCulture);
            }
        };
    }

    /// <summary>The address and port the game must send to; the guide card shows it and offers the copy command.</summary>
    public DataOutViewModel DataOut { get; }

    /// <summary>Opens the step-by-step "Connect your game" guide with its live checklist.</summary>
    [RelayCommand]
    private void OpenSetupGuide() => _guide?.Open();

    // ---- Connection: UDP port -------------------------------------------------------------------

    /// <summary>The text in the port box. [NotifyPropertyChangedFor] and [NotifyCanExecuteChangedFor]: the error and the Apply button depend on it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortError))]
    [NotifyPropertyChangedFor(nameof(HasPortError))]
    [NotifyCanExecuteChangedFor(nameof(ApplyPortCommand))]
    private string _portText;

    /// <summary>The localized validation message, or null when the text is a valid port.</summary>
    public string? PortError => TryParsePort(PortText, out _) ? null : Strings.PortInvalid;

    public bool HasPortError => PortError is not null;

    /// <summary>Help text under the box, naming the default port.</summary>
    public string PortHint => string.Format(CultureInfo.CurrentCulture, Strings.PortHint, AppSettings.Default.UdpPort);

    /// <summary>Parses text such as <c>5300</c> as a valid UDP port (1..65535; no signs, spaces or decimals).</summary>
    public static bool TryParsePort(string? text, out int port)
    {
        // NumberStyles.None: digits only (no sign, spaces or separators). `port` is 0 unless the whole text is a valid port.
        var valid = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && AppSettings.IsValidPort(parsed);
        port = valid ? parsed : 0;
        return valid;
    }

    // Enabled only for a valid port that differs from the one in use: there is nothing to apply otherwise.
    private bool CanApplyPort() => TryParsePort(PortText, out var port) && port != _settings.Current.UdpPort;

    [RelayCommand(CanExecute = nameof(CanApplyPort))]
    private void ApplyPort()
    {
        if (TryParsePort(PortText, out var port))
        {
            _settings.Update(s => s with { UdpPort = port });
            ApplyPortCommand.NotifyCanExecuteChanged(); // the stored port now equals the box: disable Apply again
        }
    }

    // ---- Units --------------------------------------------------------------------------------

    public IReadOnlyList<ChoiceOption<SpeedUnit>> SpeedUnitOptions { get; } =
    [
        new(SpeedUnit.Kmh, Strings.SpeedUnit),
        new(SpeedUnit.Mph, Strings.SpeedUnitMph)
    ];

    public IReadOnlyList<ChoiceOption<TemperatureUnit>> TemperatureUnitOptions { get; } =
    [
        new(TemperatureUnit.Celsius, Strings.TemperatureUnit),
        new(TemperatureUnit.Fahrenheit, Strings.TemperatureUnitFahrenheit)
    ];

    [ObservableProperty]
    private SpeedUnit _speedUnit;

    // CommunityToolkit.Mvvm calls this generated partial method after the property changed (and only when the value really
    // differs), which is where "the user picked something" becomes "the settings changed".
    partial void OnSpeedUnitChanged(SpeedUnit value) => _settings.Update(s => s with { SpeedUnit = value });

    [ObservableProperty]
    private TemperatureUnit _temperatureUnit;

    partial void OnTemperatureUnitChanged(TemperatureUnit value) => _settings.Update(s => s with { TemperatureUnit = value });

    // ---- Language -----------------------------------------------------------------------------

    public IReadOnlyList<ChoiceOption<AppLanguage>> LanguageOptions { get; } =
    [
        new(AppLanguage.System, Strings.LanguageSystem),
        new(AppLanguage.English, Strings.LanguageEnglish),
        new(AppLanguage.Spanish, Strings.LanguageSpanish)
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageChangePending))]
    [NotifyPropertyChangedFor(nameof(CanRestart))]
    private AppLanguage _language;

    partial void OnLanguageChanged(AppLanguage value) => _settings.Update(s => s with { Language = value });

    /// <summary>
    /// True when the chosen language would show DIFFERENT text from the one on screen, so the "applies after restart" note
    /// shows. It compares what each choice resolves to, not the enum: "System" and "English" are the same on an English PC.
    /// </summary>
    public bool LanguageChangePending =>
        !LanguageService.ResolvesToSameDisplayLanguage(Language, _activeLanguage, _systemCulture);

    /// <summary>True when a restart can be offered (a language change is pending and the app can restart itself).</summary>
    public bool CanRestart => LanguageChangePending && _restarter is not null;

    [RelayCommand]
    private void Restart() => _restarter?.Restart();

    // ---- Startup ------------------------------------------------------------------------------

    [ObservableProperty]
    private bool _startListeningOnLaunch;

    partial void OnStartListeningOnLaunchChanged(bool value) =>
        _settings.Update(s => s with { StartListeningOnLaunch = value });

    // ---- Diagnostics --------------------------------------------------------------------------

    /// <summary>Where the log files are, shown under the button.</summary>
    public string LogDirectory { get; }

    [RelayCommand]
    private void OpenLogsFolder() => _folderOpener?.TryOpen(LogDirectory);

    // ---- Data Out guide ---------------------------------------------------------------------------

    /// <summary>Step 3 of the in-game setup, quoting this PC's address and the port in use.</summary>
    public string GuideEndpointStep =>
        string.Format(CultureInfo.CurrentCulture, Strings.GuideStep3, DataOut.Address, DataOut.Port);

    /// <summary>The firewall hint, quoting the port in use.</summary>
    public string FirewallNote => string.Format(CultureInfo.CurrentCulture, Strings.GuideNoteFirewall, DataOut.Port);

    // ---- About --------------------------------------------------------------------------------

    public string AppName => AppInfo.Name;

    public string VersionText => string.Format(CultureInfo.CurrentCulture, Strings.AboutVersion, AppInfo.Version);

    public string LicenseText => string.Format(CultureInfo.CurrentCulture, Strings.AboutLicense, AppInfo.LicenseName);
}
