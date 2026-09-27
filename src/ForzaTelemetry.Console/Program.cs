// Top-level statements, no explicit `namespace` declaration: this file's project is named
// "ForzaTelemetry.Console", but declaring a real `namespace ForzaTelemetry.Console { }` in code
// would shadow the BCL's `System.Console` type inside this same file (CS0118, "Console is a
// namespace but is used like a type"). Top-level statements never introduce that namespace, so
// plain `Console.WriteLine` below keeps meaning `System.Console` with zero ceremony.

using System.Diagnostics;
using ForzaTelemetry.Core;
using ForzaTelemetry.Udp;

var port = UdpTelemetryListener.DefaultPort;
var rawMode = false;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedPort))
    {
        port = parsedPort;
        i++;
    }
    else if (args[i] == "--raw")
    {
        rawMode = true;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Without this, Ctrl+C would terminate the process immediately (the default SIGINT
    // behavior), skipping our own cleanup and printing an abrupt console close instead of a
    // graceful "stopped listening" message. Setting e.Cancel = true tells the runtime "this
    // signal was handled", so the process keeps running and our own token drives the shutdown.
    e.Cancel = true;
    cts.Cancel();
};

PrintBanner(port, rawMode);

await using var listener = new UdpTelemetryListener(port);

try
{
    if (rawMode)
    {
        await RunRawDiagnosticsAsync(listener, cts.Token);
    }
    else
    {
        await RunTelemetryViewAsync(listener, cts.Token);
    }
}
catch (OperationCanceledException)
{
    // Belt-and-suspenders: the listener's own streams already end cleanly on cancellation (see
    // UdpTelemetryListener.ReadDatagramsAsync), but this still catches the rare case where
    // cancellation surfaces between iterations instead of inside the listener, so Ctrl+C always
    // prints the same clean goodbye instead of occasionally printing a stack trace.
}

Console.WriteLine();
Console.WriteLine("Stopped listening.");

static void PrintBanner(int port, bool rawMode)
{
    Console.WriteLine("Forza Horizon 6 telemetry console");
    Console.WriteLine("==================================");
    Console.WriteLine($"Listening on UDP port {port}{(rawMode ? " (raw diagnostic mode)" : "")}.");
    Console.WriteLine();
    Console.WriteLine("In-game setup: Settings -> HUD and Gameplay -> Data Out:");
    Console.WriteLine("  Data Out: ON");
    Console.WriteLine("  Data Out IP Address: 127.0.0.1");
    Console.WriteLine($"  Data Out IP Port: {port}");
    Console.WriteLine();
    Console.WriteLine("Packets only arrive once you are actively driving (not while in menus).");
    Console.WriteLine("Press Ctrl+C to stop.");
    Console.WriteLine();
}

static async Task RunTelemetryViewAsync(UdpTelemetryListener listener, CancellationToken ct)
{
    // Rate limiting: the game can send up to ~60 packets/sec, but nobody can usefully read more
    // than about 10 console lines/sec — printing every packet would just be noise scrolling by.
    var minPrintInterval = TimeSpan.FromSeconds(1.0 / 10);
    var stopwatch = Stopwatch.StartNew();
    var lastPrint = TimeSpan.Zero;

    // A separate one-second measurement window for the displayed packets/sec figure, independent
    // of how often we actually print (otherwise the rate would just describe our own throttling).
    var rateWindowStart = stopwatch.Elapsed;
    var packetsInWindow = 0;
    var packetsPerSecond = 0.0;

    await foreach (var packet in listener.ReadPacketsAsync(ct))
    {
        packetsInWindow++;
        var elapsedInWindow = stopwatch.Elapsed - rateWindowStart;
        if (elapsedInWindow >= TimeSpan.FromSeconds(1))
        {
            packetsPerSecond = packetsInWindow / elapsedInWindow.TotalSeconds;
            packetsInWindow = 0;
            rateWindowStart = stopwatch.Elapsed;
        }

        if (stopwatch.Elapsed - lastPrint < minPrintInterval)
        {
            continue;
        }

        lastPrint = stopwatch.Elapsed;

        Console.WriteLine(
            $"RaceOn={(packet.IsRaceOn ? "Y" : "N")} " +
            $"Gear={packet.Gear,3} " +
            $"RPM={packet.CurrentEngineRpm,6:F0}/{packet.EngineMaxRpm,6:F0} " +
            $"Speed={packet.SpeedKmh,6:F1}km/h " +
            $"Accel={packet.Accel,3} " +
            $"Brake={packet.Brake,3} " +
            $"Steer={packet.Steer,4} " +
            $"pkt/s={packetsPerSecond,5:F1} " +
            $"Discarded={listener.DiscardedCount}");
    }
}

static async Task RunRawDiagnosticsAsync(UdpTelemetryListener listener, CancellationToken ct)
{
    // DIAGNOSTIC mode: byte 323 is the trailing padding byte TelemetryParser skips over (see its
    // PacketSize doc comment) — it is not exposed on TelemetryPacket at all, so the only way to
    // sanity-check that "it's alignment padding" inference against real game traffic is to look
    // at the raw datagram directly. Byte 319 (Gear) is shown alongside it purely for a second,
    // independent confirmation that offsets line up with what the parsed view reports.
    const int PaddingByteOffset = TelemetryParser.PacketSize - 1; // 323
    const int GearByteOffset = 319;

    await foreach (var datagram in listener.ReadDatagramsAsync(ct))
    {
        var length = datagram.Length;
        var span = datagram.Span;
        var paddingByte = length > PaddingByteOffset ? span[PaddingByteOffset].ToString() : "n/a (too short)";
        var gearByte = length > GearByteOffset ? span[GearByteOffset].ToString() : "n/a (too short)";

        Console.WriteLine($"[raw] length={length,4} byte[323]={paddingByte,-14} byte[319 Gear]={gearByte}");
    }
}
