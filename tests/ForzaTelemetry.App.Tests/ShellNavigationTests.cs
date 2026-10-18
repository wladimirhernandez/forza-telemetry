using ForzaTelemetry.App.Resources;
using ForzaTelemetry.App.Services;

namespace ForzaTelemetry.App.Tests;

public class ShellNavigationTests
{
    private static ShellViewModel NewShell() =>
        new(new TelemetryService(() => new FakeTelemetrySource()), TimeProvider.System);

    [Fact]
    public void Tabs_are_in_the_documented_order_and_Dashboard_is_selected_first()
    {
        var shell = NewShell();

        Assert.Equal(
            [PageId.Dashboard, PageId.Performance, PageId.Laps, PageId.Sessions, PageId.Analysis, PageId.Map, PageId.Tuning, PageId.Settings],
            shell.Tabs.Select(t => t.Id));
        Assert.Same(shell.Tabs[0], shell.SelectedTab);
        Assert.Same(shell.Dashboard, shell.CurrentPage);
    }

    [Fact]
    public void Each_tab_has_a_title_and_a_Ctrl_number_shortcut_that_follows_its_position()
    {
        var shell = NewShell();

        for (var i = 0; i < shell.Tabs.Count; i++)
        {
            Assert.False(string.IsNullOrWhiteSpace(shell.Tabs[i].Title));
            Assert.Equal($"Ctrl+{i + 1}", shell.Tabs[i].Shortcut);
            Assert.Contains(shell.Tabs[i].Shortcut, shell.Tabs[i].ToolTip);
        }
    }

    [Fact]
    public void Navigate_selects_the_tab_and_CurrentPage_follows_with_a_change_notification()
    {
        var shell = NewShell();
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.NavigateCommand.Execute(PageId.Laps);

        Assert.Equal(PageId.Laps, shell.SelectedTab.Id);
        Assert.Contains(nameof(ShellViewModel.CurrentPage), changed);
        var placeholder = Assert.IsType<PlaceholderPageViewModel>(shell.CurrentPage);
        Assert.Equal(PageId.Laps, placeholder.Id);
        Assert.Equal(Strings.TabLaps, placeholder.Title);
        Assert.Equal(Strings.LapsPlaceholder, placeholder.Description);

        shell.NavigateCommand.Execute(PageId.Dashboard);
        Assert.Same(shell.Dashboard, shell.CurrentPage);
    }

    [Fact]
    public void Setting_SelectedTab_directly_as_the_tab_strip_does_switches_the_page()
    {
        var shell = NewShell();

        shell.SelectedTab = shell.Tabs[5];

        Assert.Equal(PageId.Map, shell.SelectedTab.Id);
        Assert.IsType<PlaceholderPageViewModel>(shell.CurrentPage);
    }

    [Fact]
    public void Every_placeholder_screen_says_what_it_will_do()
    {
        var shell = NewShell();

        foreach (var tab in shell.Tabs.Where(t => t.Id != PageId.Dashboard))
        {
            var page = Assert.IsType<PlaceholderPageViewModel>(tab.Page);
            Assert.Equal(tab.Id, page.Id);
            Assert.False(string.IsNullOrWhiteSpace(page.Description));
            Assert.False(string.IsNullOrWhiteSpace(page.IconPathData));
        }
    }

    [Fact]
    public async Task The_dashboard_keeps_updating_while_another_tab_is_open()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);
        var shell = new ShellViewModel(service, TimeProvider.System);
        await shell.StartCommand.ExecuteAsync(null);
        shell.NavigateCommand.Execute(PageId.Settings);

        fake.Publish(TestPackets.Sample(currentEngineRpm: 4_321f, gear: 3));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        shell.Refresh();
        shell.NavigateCommand.Execute(PageId.Dashboard);

        Assert.Equal(4_321f, shell.Dashboard.Rpm); // no stale numbers when the user comes back
        await shell.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public void Copy_command_puts_the_primary_address_on_the_clipboard_and_is_disabled_without_one()
    {
        var clipboard = new FakeClipboard();
        var service = new TelemetryService(() => new FakeTelemetrySource());

        var withAddress = new ShellViewModel(
            service, TimeProvider.System, new FixedNetworkInfo("192.168.0.204", "10.0.0.7"), clipboard: clipboard);
        Assert.True(withAddress.DataOut.CopyAddressCommand.CanExecute(null));
        withAddress.DataOut.CopyAddressCommand.Execute(null);
        Assert.Equal(["192.168.0.204"], clipboard.Copied);

        var without = new ShellViewModel(service, TimeProvider.System, new FixedNetworkInfo(), clipboard: clipboard);
        Assert.False(without.DataOut.CopyAddressCommand.CanExecute(null));
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public List<string> Copied { get; } = [];

        public bool TrySetText(string text)
        {
            Copied.Add(text);
            return true;
        }
    }

    private sealed class FixedNetworkInfo(params string[] ips) : INetworkInfo
    {
        public IReadOnlyList<LanAddress> GetLanAddresses() =>
            ips.Select(ip => new LanAddress("Wi-Fi", System.Net.IPAddress.Parse(ip))).ToList();
    }
}
