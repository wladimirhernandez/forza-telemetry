using CommunityToolkit.Mvvm.ComponentModel;

namespace ForzaTelemetry.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    // Placeholder for T1 (scaffold only). T2 replaces this with the live telemetry-driven status
    // (RPM, speed, gear, etc.) fed by TelemetryService.
    [ObservableProperty]
    public partial string Status { get; set; } = "Forza Telemetry — waiting for data";
}
