using System.Net.Sockets;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;

namespace ForzaTelemetry.App.Tests;

public class TelemetryServiceTests
{
    [Fact]
    public async Task StartAsync_receives_packets_published_by_the_source()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);

        await service.StartAsync();
        fake.Publish(TestPackets.Sample(gear: 5));

        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        Assert.True(service.IsRunning);
        Assert.Equal((byte)5, service.Latest?.Gear);
        Assert.Null(service.Fault);

        await service.StopAsync();
    }

    [Fact]
    public async Task StopAsync_then_StartAsync_again_builds_a_fresh_source_and_keeps_working()
    {
        var firstSource = new FakeTelemetrySource();
        var secondSource = new FakeTelemetrySource();
        var sourcesToHandOut = new Queue<ITelemetrySource>([firstSource, secondSource]);
        var service = new TelemetryService(() => sourcesToHandOut.Dequeue());

        await service.StartAsync();
        firstSource.Publish(TestPackets.Sample(gear: 1));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));

        await service.StopAsync();
        Assert.False(service.IsRunning);

        // Safe to Start again: a brand-new source is built from the factory.
        await service.StartAsync();
        Assert.True(service.IsRunning);
        secondSource.Publish(TestPackets.Sample(gear: 2));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 2));
        Assert.Equal((byte)2, service.Latest?.Gear);

        await service.StopAsync();
    }

    [Fact]
    public async Task StartAsync_records_a_fault_when_the_factory_throws_instead_of_propagating_it()
    {
        var service = new TelemetryService(
            () => throw new SocketException((int)SocketError.AddressAlreadyInUse));

        // This must NOT throw: a bad Start becomes a recorded Fault, not an exception out of
        // StartAsync — see TelemetryService's remarks on why exceptions must never escape unobserved.
        await service.StartAsync();

        Assert.False(service.IsRunning);
        var fault = Assert.IsType<SocketException>(service.Fault);
        Assert.Equal(SocketError.AddressAlreadyInUse, fault.SocketErrorCode);
    }

    [Fact]
    public async Task A_mid_stream_exception_from_the_source_is_captured_as_a_fault()
    {
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(() => fake);

        await service.StartAsync();
        fake.FailNextReadWith(new InvalidOperationException("simulated mid-stream failure"));
        fake.Publish(TestPackets.Sample());

        Assert.True(await Wait.UntilAsync(() => service.Fault is not null));
        Assert.False(service.IsRunning);
        Assert.IsType<InvalidOperationException>(service.Fault);
    }

    [Fact]
    public async Task StartAsync_after_a_mid_stream_fault_releases_the_old_source_and_recovers()
    {
        var firstSource = new FakeTelemetrySource();
        var secondSource = new FakeTelemetrySource();
        var sourcesToHandOut = new Queue<ITelemetrySource>([firstSource, secondSource]);
        var service = new TelemetryService(() => sourcesToHandOut.Dequeue());

        await service.StartAsync();
        firstSource.FailNextReadWith(new InvalidOperationException("simulated mid-stream failure"));
        firstSource.Publish(TestPackets.Sample());
        Assert.True(await Wait.UntilAsync(() => service.Fault is not null && !service.IsRunning));

        // No StopAsync in between: the UI disables Stop once the loop has faulted, so Start must
        // clean up the leftovers itself. With the real UDP adapter, a leaked source keeps the port.
        await service.StartAsync();

        Assert.True(firstSource.IsDisposed);
        Assert.Null(service.Fault);
        secondSource.Publish(TestPackets.Sample(gear: 4));
        Assert.True(await Wait.UntilAsync(() => service.Latest?.Gear == 4));

        await service.StopAsync();
    }

    [Fact]
    public async Task StopAsync_before_any_Start_is_a_safe_no_op()
    {
        var service = new TelemetryService(() => new FakeTelemetrySource());

        await service.StopAsync();

        Assert.False(service.IsRunning);
        Assert.Null(service.Latest);
    }
}
