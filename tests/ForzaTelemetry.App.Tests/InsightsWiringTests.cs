using System.Net.Sockets;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using ForzaTelemetry.Core.Insights;

namespace ForzaTelemetry.App.Tests;

/// <summary>
/// The Analysis, Map and Tuning pages as the app wires them: the engines are registered with the service, the shell shows the
/// real pages, only the visible one is refreshed, and a new session resets what belongs to the session (not the dyno curves).
/// </summary>
public class InsightsWiringTests
{
    private sealed class MemoryStore : ISettingsStore
    {
        public AppSettings Load() => AppSettings.Default;

        public bool TrySave(AppSettings settings) => true;
    }

    /// <summary>Everything the composition root builds, minus the window: engines, the three pages, a service and a shell.</summary>
    private sealed class Rig
    {
        public Rig()
        {
            Engines = new TelemetryEngines();
            var units = new UnitsService();
            Analysis = new AnalysisViewModel(Engines.History, units);
            Map = new MapViewModel(Engines.Path, Engines.History, units);
            Tuning = new TuningViewModel(Engines.Dyno, Engines.History, Engines.Bottoming, units);
            Settings = new SettingsService(new MemoryStore(), AppSettings.Default);
            Service = new TelemetryService(
                () =>
                {
                    if (BusyPorts.Contains(Settings.Current.UdpPort))
                    {
                        throw new SocketException((int)SocketError.AddressAlreadyInUse);
                    }

                    // A fresh source per Start, like the real listener factory: a stopped source is disposed for good.
                    return Source = new FakeTelemetrySource();
                },
                observers: Engines.Observers);
            Shell = new ShellViewModel(
                Service,
                TimeProvider.System,
                dataOut: new DataOutViewModel(Settings.Current.UdpPort, null),
                settings: Settings,
                analysisPage: Analysis,
                mapPage: Map,
                tuningPage: Tuning);
        }

        public TelemetryEngines Engines { get; }

        public AnalysisViewModel Analysis { get; }

        public MapViewModel Map { get; }

        public TuningViewModel Tuning { get; }

        public SettingsService Settings { get; }

        public FakeTelemetrySource Source { get; private set; } = new();

        public TelemetryService Service { get; }

        public ShellViewModel Shell { get; }

        public HashSet<int> BusyPorts { get; } = [];

        /// <summary>Feeds a short drive straight into the engines (the receive loop's job), with a dyno pull and a bottoming hit.</summary>
        public void Drive()
        {
            for (var i = 0; i < 10; i++)
            {
                var packet = InsightPackets.Pull(3000 + (i * 400), 150_000 + (i * 5_000), 300, carOrdinal: 2211, performanceIndex: 700)
                    with
                {
                    TimestampMS = (uint)(1000 + (i * 16)),
                    PositionX = i * 10,
                    PositionZ = i * 5,
                    NormalizedSuspensionTravelFrontLeft = i == 4 ? 1f : 0f
                };
                foreach (var observer in Engines.Observers)
                {
                    observer.OnPacket(packet);
                }
            }
        }
    }

    // ---- Registration -------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_insights_engine_is_a_registered_observer_that_the_service_feeds()
    {
        var rig = new Rig();
        await rig.Service.StartAsync();

        for (var i = 0; i < 10; i++)
        {
            rig.Source.Publish(
                InsightPackets.Pull(3000 + (i * 400), 150_000, 300, carOrdinal: 2211, performanceIndex: 700)
                with
                {
                    TimestampMS = (uint)(1000 + (i * 16)),
                    PositionX = i * 10,
                    PositionZ = i * 5,
                    NormalizedSuspensionTravelFrontLeft = i == 4 ? 1f : 0f
                });
        }

        Assert.True(await Wait.UntilAsync(() => rig.Service.ReceivedCount == 10));
        Assert.Equal(10, rig.Engines.History.Count);
        Assert.True(rig.Engines.Path.Count >= 2);
        Assert.Equal(10, rig.Engines.Dyno.GetCurve(2211, 700).Count);
        Assert.Equal(1, rig.Engines.Bottoming.Counts.FrontLeft);
        await rig.Service.StopAsync();
    }

    [Fact]
    public void The_observer_list_holds_each_insights_engine_once()
    {
        var engines = new TelemetryEngines();

        Assert.Equal(8, engines.Observers.Count);
        Assert.Contains(engines.History, engines.Observers);
        Assert.Contains(engines.Path, engines.Observers);
        Assert.Contains(engines.Dyno, engines.Observers);
        Assert.Contains(engines.Bottoming, engines.Observers);
        Assert.Equal(engines.Observers.Count, engines.Observers.Distinct().Count());
    }

    // ---- Tabs ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_analysis_map_and_tuning_tabs_show_the_real_pages_not_placeholders()
    {
        var rig = new Rig();

        Assert.Same(rig.Analysis, rig.Shell.Tabs.Single(t => t.Id == PageId.Analysis).Page);
        Assert.Same(rig.Map, rig.Shell.Tabs.Single(t => t.Id == PageId.Map).Page);
        Assert.Same(rig.Tuning, rig.Shell.Tabs.Single(t => t.Id == PageId.Tuning).Page);
        Assert.Same(rig.Analysis, rig.Shell.Analysis);
        Assert.Same(rig.Map, rig.Shell.Map);
        Assert.Same(rig.Tuning, rig.Shell.Tuning);
    }

    [Fact]
    public void Without_those_pages_the_tabs_still_show_their_placeholders()
    {
        var shell = new ShellViewModel(new TelemetryService(() => new FakeTelemetrySource()), TimeProvider.System);

        foreach (var id in new[] { PageId.Analysis, PageId.Map, PageId.Tuning })
        {
            Assert.IsType<PlaceholderPageViewModel>(shell.Tabs.Single(t => t.Id == id).Page);
        }
    }

    [Fact]
    public void With_the_insights_pages_wired_the_sessions_tab_is_the_placeholder_that_remains_of_them()
    {
        var rig = new Rig();

        var placeholders = rig.Shell.Tabs.Where(t => t.Page is PlaceholderPageViewModel).Select(t => t.Id).ToList();

        Assert.DoesNotContain(PageId.Analysis, placeholders);
        Assert.DoesNotContain(PageId.Map, placeholders);
        Assert.DoesNotContain(PageId.Tuning, placeholders);
        Assert.Contains(PageId.Sessions, placeholders); // still to come
    }

    [Fact]
    public void The_three_pages_are_refreshable_pages()
    {
        var rig = new Rig();

        Assert.IsAssignableFrom<IRefreshablePage>(rig.Analysis);
        Assert.IsAssignableFrom<IRefreshablePage>(rig.Map);
        Assert.IsAssignableFrom<IRefreshablePage>(rig.Tuning);
    }

    // ---- Refresh routing ----------------------------------------------------------------------------------

    [Fact]
    public void A_tick_refreshes_only_the_page_that_is_showing()
    {
        var rig = new Rig();
        rig.Drive();

        // Dashboard is showing: none of the expensive pages is touched.
        rig.Shell.Refresh();
        Assert.False(rig.Analysis.HasData);
        Assert.False(rig.Map.HasCurrent);
        Assert.False(rig.Tuning.HasData);

        rig.Shell.NavigateCommand.Execute(PageId.Analysis);
        rig.Shell.Refresh();
        Assert.True(rig.Analysis.HasData);
        Assert.False(rig.Map.HasCurrent); // hidden: untouched
        Assert.False(rig.Tuning.HasData);

        rig.Shell.NavigateCommand.Execute(PageId.Map);
        rig.Shell.Refresh();
        Assert.True(rig.Map.HasCurrent);
        Assert.False(rig.Tuning.HasData);

        rig.Shell.NavigateCommand.Execute(PageId.Tuning);
        rig.Shell.Refresh();
        Assert.True(rig.Tuning.HasData);
    }

    [Fact]
    public void Returning_to_a_tab_brings_its_page_up_to_date_on_the_next_tick()
    {
        var rig = new Rig();
        rig.Shell.NavigateCommand.Execute(PageId.Analysis);
        rig.Shell.Refresh(); // nothing yet
        rig.Shell.NavigateCommand.Execute(PageId.Dashboard);
        rig.Drive(); // data arrives while another tab is open

        rig.Shell.NavigateCommand.Execute(PageId.Analysis);
        rig.Shell.Refresh();

        Assert.True(rig.Analysis.HasData);
        Assert.Equal(10, rig.Analysis.SampleCount);
    }

    [Fact]
    public async Task The_visible_page_is_refreshed_even_while_the_listener_is_faulted()
    {
        var rig = new Rig();
        rig.Drive();
        rig.BusyPorts.Add(rig.Settings.Current.UdpPort);
        await rig.Shell.StartCommand.ExecuteAsync(null); // fails: port in use
        rig.Shell.NavigateCommand.Execute(PageId.Analysis);

        rig.Shell.Refresh();

        Assert.NotNull(rig.Service.Fault);
        Assert.True(rig.Analysis.HasData); // the session before the failure is still readable
    }

    // ---- Session semantics --------------------------------------------------------------------------------

    [Fact]
    public async Task A_fresh_start_clears_history_path_and_bottoming_but_keeps_the_dyno_curves()
    {
        var rig = new Rig();
        rig.Drive();
        rig.Shell.NavigateCommand.Execute(PageId.Tuning);
        rig.Shell.Refresh();
        Assert.True(rig.Engines.History.Count > 0);
        Assert.True(rig.Engines.Path.Count > 0);
        Assert.Equal(1, rig.Engines.Bottoming.Counts.FrontLeft);
        Assert.NotEmpty(rig.Engines.Dyno.GetCurve(2211, 700));

        await rig.Shell.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, rig.Engines.History.Count);
        Assert.Equal(0, rig.Engines.Path.Count);
        Assert.Equal(0, rig.Engines.Bottoming.Counts.Total);
        Assert.NotEmpty(rig.Engines.Dyno.GetCurve(2211, 700)); // a curve describes the car, not the session
        Assert.False(rig.Analysis.HasData);
        Assert.False(rig.Map.HasData);
        await rig.Shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task A_start_that_fails_keeps_the_previous_sessions_traces_path_and_tallies()
    {
        var rig = new Rig();
        rig.Drive();
        rig.BusyPorts.Add(rig.Settings.Current.UdpPort);

        await rig.Shell.StartCommand.ExecuteAsync(null);

        Assert.NotNull(rig.Service.Fault);
        Assert.Equal(10, rig.Engines.History.Count);
        Assert.True(rig.Engines.Path.Count > 0);
        Assert.Equal(1, rig.Engines.Bottoming.Counts.FrontLeft);
    }

    [Fact]
    public async Task Moving_the_port_continues_the_session_without_clearing_the_insights_data()
    {
        var rig = new Rig();
        await rig.Shell.StartCommand.ExecuteAsync(null);
        rig.Drive();

        rig.Settings.Update(s => s with { UdpPort = 5400 });
        Assert.True(await Wait.UntilAsync(() => !rig.Shell.IsBusy && rig.Service.IsRunning));

        Assert.Equal(10, rig.Engines.History.Count);
        Assert.True(rig.Engines.Path.Count > 0);
        await rig.Shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Stopping_keeps_the_data_readable()
    {
        var rig = new Rig();
        await rig.Shell.StartCommand.ExecuteAsync(null);
        rig.Drive();

        await rig.Shell.StopCommand.ExecuteAsync(null);

        Assert.Equal(10, rig.Engines.History.Count);
        Assert.True(rig.Engines.Path.Count > 0);
    }

    [Fact]
    public async Task The_dyno_curve_of_a_car_survives_a_stop_and_a_start_and_shows_on_the_tuning_page()
    {
        var rig = new Rig();
        rig.Drive();
        await rig.Shell.StartCommand.ExecuteAsync(null);
        await rig.Shell.StopCommand.ExecuteAsync(null);
        await rig.Shell.StartCommand.ExecuteAsync(null);

        Assert.Equal(10, rig.Engines.Dyno.GetCurve(2211, 700).Count);
        rig.Shell.NavigateCommand.Execute(PageId.Tuning);
        rig.Engines.Dyno.OnPacket(InsightPackets.Pull(4000, 160_000, 310, carOrdinal: 2211, performanceIndex: 700));
        rig.Shell.Refresh();

        Assert.True(rig.Tuning.HasCurve);
        await rig.Shell.StopCommand.ExecuteAsync(null);
    }
}
