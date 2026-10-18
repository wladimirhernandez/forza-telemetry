# Forza Telemetry

A live telemetry dashboard for Forza Horizon 6 on Windows. See your car, tires, shift lights, G-force, inputs and engine data in real time, straight from the game's Data Out feature.

> *Forza Telemetry* is a working title and may change before the first release.

**Unofficial. Not affiliated with or endorsed by Microsoft, Xbox Game Studios, Playground Games or Turn 10. Forza and Forza Horizon are trademarks of Microsoft.**

## Features

| Area | Status |
| --- | --- |
| **Dashboard**: live car and tire view, shift lights, gear and speed, pedal and steering inputs, G-force, engine gauges | Available |
| **Settings**: UDP port, this PC's IP address, Data Out setup help | Available |
| **Performance**: 0-100 and 0-200 km/h, quarter mile, top speed | Coming in v1.0 |
| **Laps**: best, last and current lap with live delta | Coming in v1.0 |
| **Sessions**: record a drive and replay it later | Coming in v1.0 |
| **Analysis**: live speed, RPM, throttle, brake and steering traces | Coming in v1.0 |
| **Map**: the path you drove, colored by speed | Coming in v1.0 |
| **Tuning**: power and torque curves, understeer and oversteer hints | Coming in v1.0 |

## Screenshots

![Dashboard](docs/screenshots/dashboard.png)

_Screenshots will be added with the first release._

## Requirements

- Windows 10 or 11, 64-bit.
- Forza Horizon 6 on PC, or on Xbox connected to the same local network as the PC.

No .NET install is needed: the installer is self-contained.

## Install

1. Download the latest `ForzaTelemetry-Setup-<version>.exe` from the [GitHub Releases](../../releases) page.
2. Run it. It asks for administrator rights because it adds a Windows Firewall rule for the telemetry port.
3. Verify the download against the SHA-256 file published next to it, if you like.

Early builds are not code-signed, so Windows SmartScreen may warn that the publisher is unknown. Choose **More info**, then **Run anyway** if you trust the source.

## Set up the game

Short version:

1. In the game open **Settings**, then **HUD and Gameplay**.
2. Turn **Data Out** on.
3. Set **Data Out IP Address** to the address shown in the app.
4. Set **Data Out IP Port** to `5300`.

The game only sends packets while you are driving, so nothing shows up in menus or while paused.

Xbox: the console and the PC must be on the same network (wired or Wi-Fi). If you use a VPN on the PC, enable its "LAN access" option. The installer creates a Windows Firewall rule for UDP 5300 on private networks only.

Full step-by-step guide with troubleshooting: [docs/data-out-setup.md](docs/data-out-setup.md).

## Build from source

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build ForzaTelemetry.slnx
dotnet test ForzaTelemetry.slnx
installer/build.ps1 -Version 0.1.0   # needs Inno Setup 6; writes the installer to dist/
```

## Architecture

- `ForzaTelemetry.Core`: pure telemetry model and packet parsing, no I/O.
- `ForzaTelemetry.Udp`: UDP listener that feeds packets to Core.
- `ForzaTelemetry.App`: the WPF application (MVVM), the composition root.
- `ForzaTelemetry.Console`: a small command-line tool that prints live telemetry, useful for debugging.

## License

Source-available under the [PolyForm Strict License 1.0.0](LICENSE). You may read and learn from the code and use the app for noncommercial purposes, but you may not modify, copy or redistribute it. This is not an open-source license.

## Credits

Built with .NET, WPF, CommunityToolkit.Mvvm and Serilog. See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for the third-party licenses.
