// Top-level statements, no explicit `namespace` declaration: this file's project is named
// "ForzaTelemetry.Console", but declaring a real `namespace ForzaTelemetry.Console { }` in code
// would shadow the BCL's `System.Console` type inside this same file (CS0118, "Console is a
// namespace but is used like a type"). Top-level statements never introduce that namespace, so
// plain `Console.WriteLine` below keeps meaning `System.Console` with zero ceremony.

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ForzaTelemetry.Core;
using ForzaTelemetry.Udp;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

var port = UdpTelemetryListener.DefaultPort;
var rawMode = false;
var verbose = false;
var unknownArgs = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port":
            if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out var parsedPort))
            {
                Console.Error.WriteLine(
                    $"Invalid --port value: '{(i + 1 < args.Length ? args[i + 1] : "<missing>")}'. " +
                    "Expected a whole number between 1 and 65535.");
                return 1;
            }

            // 0 is technically a valid argument to UdpTelemetryListener (it means "let the OS pick
            // a free port"), but that is only useful for tests running several listeners side by
            // side — a real user pointing their Xbox at a port they never learn the value of makes
            // no sense, so this console entry point rejects it explicitly.
            if (parsedPort is < 1 or > 65535)
            {
                Console.Error.WriteLine($"Invalid --port value: {parsedPort}. Must be between 1 and 65535.");
                return 1;
            }

            port = parsedPort;
            i++;
            break;

        case "--raw":
            rawMode = true;
            break;

        case "--verbose":
            verbose = true;
            break;

        default:
            unknownArgs.Add(args[i]);
            break;
    }
}

if (unknownArgs.Count > 0)
{
    Console.WriteLine($"Warning: ignoring unknown argument(s): {string.Join(", ", unknownArgs)}");
    Console.WriteLine();
}

var logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
Directory.CreateDirectory(logDirectory);
var logFilePathTemplate = Path.Combine(logDirectory, "telemetry-.log"); // Serilog inserts the date before ".log"

var minimumLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Information;

// An explicit Serilog logger instance (not the static Serilog.Log.Logger) is passed straight into
// AddSerilog below with dispose: true — this avoids relying on process-wide mutable state (nobody
// else in this small app needs to reach the logger through Serilog's static ambient Log class) and
// means the ILoggerFactory's own disposal (the `using` below) is what flushes and closes the file,
// equivalent to calling Serilog's Log.CloseAndFlush() but scoped to this one instance.
var serilogLogger = new LoggerConfiguration()
    .MinimumLevel.Is(minimumLevel)
    .WriteTo.Console()
    .WriteTo.File(logFilePathTemplate, rollingInterval: RollingInterval.Day)
    .CreateLogger();

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(serilogLogger, dispose: true));
var appLogger = loggerFactory.CreateLogger("ForzaTelemetry.Console");
var listenerLogger = loggerFactory.CreateLogger<UdpTelemetryListener>();

var lanAddresses = GetLanIPv4Addresses();

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

PrintBanner(port, rawMode, lanAddresses, logFilePathTemplate);
appLogger.LogInformation(
    "This PC's LAN IPv4 addresses: {Addresses}",
    lanAddresses.Count == 0
        ? "(none found)"
        : string.Join(", ", lanAddresses.Select(a => $"{a.Address} ({a.InterfaceName})")));

UdpTelemetryListener listener;
try
{
    listener = new UdpTelemetryListener(port, logger: listenerLogger);
}
catch (SocketException ex)
{
    var message = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
        ? $"Port {port} is already in use — is another instance or telemetry tool running?"
        : $"Could not bind UDP port {port}: {ex.SocketErrorCode} ({ex.Message}).";

    appLogger.LogError(ex, "{Message}", message);
    Console.Error.WriteLine(message);
    return 1;
}

await using (listener)
{
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
}

Console.WriteLine();
Console.WriteLine("Stopped listening.");

return 0;

static void PrintBanner(
    int port,
    bool rawMode,
    IReadOnlyList<(string InterfaceName, IPAddress Address)> lanAddresses,
    string logFilePathTemplate)
{
    Console.WriteLine("Forza Horizon 6 telemetry console");
    Console.WriteLine("==================================");
    Console.WriteLine($"Listening on UDP port {port}{(rawMode ? " (raw diagnostic mode)" : "")}.");
    Console.WriteLine($"Logging to console and to: {logFilePathTemplate}");
    Console.WriteLine();
    Console.WriteLine("In-game setup: Settings -> HUD and Gameplay -> Data Out:");
    Console.WriteLine("  Data Out: ON");
    Console.WriteLine("  Data Out IP Address: one of this PC's addresses below");
    Console.WriteLine($"  Data Out IP Port: {port}");
    Console.WriteLine();
    Console.WriteLine("This PC's network addresses:");
    if (lanAddresses.Count == 0)
    {
        Console.WriteLine("  (none found — no active, non-loopback network interface has an IPv4 address)");
    }
    else
    {
        foreach (var (interfaceName, address) in lanAddresses)
        {
            Console.WriteLine($"  {address,-15} ({interfaceName})");
        }
    }

    Console.WriteLine("  127.0.0.1       (this same PC only, e.g. FH6 running via emulation/streaming here)");
    Console.WriteLine();
    Console.WriteLine("Xbox: use the address of the interface on the SAME network as the console —");
    Console.WriteLine("usually Wi-Fi or Ethernet, not a VPN adapter (e.g. Surfshark/WireGuard/TAP), even");
    Console.WriteLine("if the VPN cannot be turned off: the VPN adapter is a different, unrelated address.");
    Console.WriteLine();
    Console.WriteLine("Packets only arrive once you are actively driving (not while in menus).");
    Console.WriteLine("Press Ctrl+C to stop.");
    Console.WriteLine();
}

static List<(string InterfaceName, IPAddress Address)> GetLanIPv4Addresses()
{
    var results = new List<(string, IPAddress)>();

    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
    {
        // Skip interfaces that are down (nothing will ever arrive on them) and loopback (127.0.0.1
        // is printed separately, unconditionally, below — it is always available regardless of
        // which NICs exist).
        if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            continue;
        }

        foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
        {
            if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
            {
                results.Add((nic.Name, unicast.Address));
            }
        }
    }

    return results;
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

        // Plain Console.WriteLine, same as the log lines above: this is a live status view, not a
        // durable record, so it does not need to go through the logger — and mixing it in with log
        // lines on the same console is fine for this didactic tool (both are just text scrolling
        // by); a real dashboard UI is what the WPF app (ForzaTelemetry.App) is for.
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
