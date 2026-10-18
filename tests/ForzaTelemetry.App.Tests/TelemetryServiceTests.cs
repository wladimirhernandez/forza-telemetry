using System.Net.Sockets;
using ForzaTelemetry.App.Services;
using ForzaTelemetry.Core;
using Microsoft.Extensions.Logging;

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

        // Fault alone is a sufficient condition: the service publishes "not running" before it publishes the fault (see
        // the next test for why that order is the contract), so asserting IsRunning right after is not a race.
        Assert.True(await Wait.UntilAsync(() => service.Fault is not null));
        Assert.False(service.IsRunning);
        Assert.IsType<InvalidOperationException>(service.Fault);
    }

    /// <summary>Logger that parks the calling thread inside an Error log call until the test releases it.</summary>
    private sealed class ParkingErrorLogger : ILogger<TelemetryService>
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new(false);

        public Task Reached => _reached.Task;

        public void Release() => _release.Set();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                _reached.TrySetResult();
                _release.Wait(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task A_published_fault_is_never_seen_together_with_IsRunning_true()
    {
        // Root cause of the flaky A_mid_stream_exception... test: the loop used to write Fault first and clear IsRunning only
        // in its finally block, so a reader polling Fault could still read IsRunning == true for a moment. Rather than hoping
        // to hit that window, this test STOPS the loop thread inside the window: the logger is called right after the fault is
        // published, and it does not return until the assertions ran. With the old order this fails every time, not 1 in 4.
        var fake = new FakeTelemetrySource();
        var logger = new ParkingErrorLogger();
        var service = new TelemetryService(() => fake, logger);

        await service.StartAsync();
        fake.FailNextReadWith(new InvalidOperationException("simulated mid-stream failure"));
        fake.Publish(TestPackets.Sample());
        await logger.Reached.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(service.Fault);
        Assert.False(service.IsRunning);

        logger.Release();
        await service.StopAsync();
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

    [Fact]
    public async Task A_second_concurrent_StopAsync_does_not_complete_before_the_source_is_disposed()
    {
        // OnExit's Stop can overlap the Stop button's Stop; the later caller must still only return once
        // the socket is really released.
        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeTelemetrySource { DisposeGate = disposeGate };
        var service = new TelemetryService(() => fake);
        await service.StartAsync();

        var firstStop = service.StopAsync();
        var secondStop = service.StopAsync();

        // Give a (buggy) early return every chance to show up while the dispose is still blocked.
        await Task.Delay(100);
        Assert.False(secondStop.IsCompleted);
        Assert.False(fake.IsDisposed);

        disposeGate.SetResult();
        await Task.WhenAll(firstStop, secondStop);

        Assert.True(fake.IsDisposed);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task Latest_is_cleared_on_Stop_and_on_Start_but_ReceivedCount_is_kept()
    {
        var firstSource = new FakeTelemetrySource();
        var secondSource = new FakeTelemetrySource();
        var sources = new Queue<ITelemetrySource>([firstSource, secondSource]);
        var service = new TelemetryService(() => sources.Dequeue());

        await service.StartAsync();
        firstSource.Publish(TestPackets.Sample(gear: 4));
        Assert.True(await Wait.UntilAsync(() => service.ReceivedCount == 1));
        Assert.NotNull(service.Latest);

        await service.StopAsync();
        Assert.Null(service.Latest);
        Assert.Equal(1, service.ReceivedCount);

        await service.StartAsync();
        Assert.Null(service.Latest);

        await service.StopAsync();
    }

    [Fact]
    public async Task Logs_start_stop_and_a_mid_stream_fault()
    {
        var fake = new FakeTelemetrySource();
        var logger = new ListLogger<TelemetryService>();
        var service = new TelemetryService(() => fake, logger);

        await service.StartAsync();
        fake.FailNextReadWith(new InvalidOperationException("boom"));
        fake.Publish(TestPackets.Sample());
        Assert.True(await Wait.UntilAsync(() => service.Fault is not null));
        await service.StopAsync();

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("started"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception?.Message == "boom");
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("stopped"));
    }

    [Fact]
    public async Task Logs_a_factory_failure_as_an_error()
    {
        var logger = new ListLogger<TelemetryService>();
        var service = new TelemetryService(() => throw new SocketException((int)SocketError.AddressAlreadyInUse), logger);

        await service.StartAsync();

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is SocketException);
    }

    [Fact]
    public async Task A_factory_failure_is_a_failed_start_and_a_mid_stream_failure_is_not()
    {
        var failStart = true;
        var fake = new FakeTelemetrySource();
        var service = new TelemetryService(
            () => failStart ? throw new SocketException((int)SocketError.AddressAlreadyInUse) : fake);

        await service.StartAsync();
        Assert.NotNull(service.Fault);
        Assert.True(service.StartFailed); // no listener ever ran: nothing was started

        failStart = false;
        await service.StartAsync();
        Assert.Null(service.Fault);
        Assert.False(service.StartFailed); // a later start clears it, like the fault

        fake.FailNextReadWith(new InvalidOperationException("boom"));
        fake.Publish(TestPackets.Sample());
        Assert.True(await Wait.UntilAsync(() => service.Fault is not null));

        Assert.False(service.StartFailed); // the listener ran and died: the session it served was real
        await service.StopAsync();
    }
}
