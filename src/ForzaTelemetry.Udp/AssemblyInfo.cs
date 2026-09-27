using System.Runtime.CompilerServices;

// Grants the test project access to internal members - specifically the test-only
// UdpTelemetryListener constructor overload that lets a test wrap the real receive with a fake one
// (e.g. to simulate a SocketException on demand). See UdpTelemetryListener's internal constructor
// doc comment for why that seam exists instead of only testing through the real network stack.
[assembly: InternalsVisibleTo("ForzaTelemetry.Udp.Tests")]
