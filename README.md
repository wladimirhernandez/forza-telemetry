<div align="center">

# Forza Telemetry

**Live telemetry for Forza Horizon 6 on Windows**

See your car, tires, shift lights, G-force, inputs and engine data in real time. Time your runs and laps, record a drive and replay it later, and get tuning hints from your own numbers, all from the game's Data Out feature.

[![Latest release](https://img.shields.io/github/v/release/wladimirhernandez/forza-telemetry?include_prereleases&label=download&color=e8402a)](https://github.com/wladimirhernandez/forza-telemetry/releases/latest)
[![CI](https://github.com/wladimirhernandez/forza-telemetry/actions/workflows/ci.yml/badge.svg)](https://github.com/wladimirhernandez/forza-telemetry/actions/workflows/ci.yml)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-0078d4)
![.NET](https://img.shields.io/badge/.NET-10-512bd4)
[![License](https://img.shields.io/badge/license-PolyForm%20Strict-lightgrey)](LICENSE)

[**Download**](#download) · [Features](#features) · [Screenshots](#screenshots) · [Set up the game](#set-up-the-game) · [Build from source](#build-from-source)

![Dashboard: tire temperatures, shift lights, gear, speed, inputs and G-force](docs/screenshots/dashboard.png)

</div>

> [!NOTE]
> Unofficial. Not affiliated with or endorsed by Microsoft, Xbox Game Studios, Playground Games or Turn 10. Forza and Forza Horizon are trademarks of Microsoft.

## Download

Grab the latest build from the **[Releases page](https://github.com/wladimirhernandez/forza-telemetry/releases/latest)**. Both options are self-contained: no .NET install needed.

| Option | File | Best for |
| --- | --- | --- |
| **Portable** | `ForzaTelemetry-<version>-win-x64-portable.zip` | Trying it out, no admin rights, running from a USB drive. |
| **Installer** | `ForzaTelemetry-Setup-<version>.exe` | Start menu shortcut, uninstaller and the firewall rule set up for you. |

### Portable

1. Download the `.zip` and extract it anywhere (Desktop, Documents, a USB drive).
2. Open the `ForzaTelemetry` folder and run **`ForzaTelemetry.App.exe`**.
3. The first time it starts listening, Windows asks whether to allow it through the firewall. Tick **Private networks** and choose **Allow**.

Nothing is installed. Your settings, car names, best times and recordings are saved in `%APPDATA%\ForzaTelemetry`; delete that folder to reset or remove everything.

### Installer

1. Download and run `ForzaTelemetry-Setup-<version>.exe`.
2. It asks for administrator rights because it adds a Windows Firewall rule for the app (UDP, private networks).

> [!TIP]
> Builds are not code-signed yet, so Windows SmartScreen may say the publisher is unknown. Choose **More info**, then **Run anyway**. Every file has a `.sha256` next to it on the release page if you want to verify the download.

## Requirements

- Windows 10 or 11, 64-bit.
- Forza Horizon 6 on PC, or on Xbox connected to the same local network as the PC.

## Features

Eight tabs, each one keyboard-reachable with <kbd>Ctrl</kbd>+<kbd>1</kbd> to <kbd>Ctrl</kbd>+<kbd>8</kbd>.

- **Real car names** from a community-maintained list, and you can rename any car.
- **Guided "Connect your game" setup** with a live checklist on first run.
- **Mini mode** (<kbd>Ctrl</kbd>+<kbd>M</kbd>): a compact always-on-top overlay with gear, speed, shift lights and your lap delta.
- **English and Spanish**, metric or imperial units.

| Tab | What it does |
| --- | --- |
| **Dashboard** | The car at a glance: tire temperatures, shift lights, gear and speed, pedal and steering inputs, G-force, engine gauges. |
| **Performance** | Times 0-100, 0-200 and 100-200 km/h (or 0-60 and 60-130 mph), the quarter mile, top speed and braking distance from 100 km/h (or 60 mph), keeping the best of each per car. Export to CSV. |
| **Laps** | Best, last and current lap with a live delta against your best, and the list of every lap. Export to CSV. |
| **Sessions** | Records what the game sends and plays it back at 0.5x to 4x, driving every other tab as if it were live. Rename, delete, or export a recording to CSV. |
| **Analysis** | Live speed, RPM, throttle, brake and steering traces on one time axis, or Lap comparison to lay your current lap over your best, by distance, with a time-gap lane. |
| **Map** | The path you drove, colored by speed, with braking points marked. Works live and on replays. |
| **Tuning** | Power and torque curves captured from full-throttle pulls, plus understeer/oversteer, tire temperature and suspension bottoming hints. |
| **Settings** | UDP port, units (km/h or mph, °C or °F, kW or hp, psi or bar), language, listen on launch, this PC's IP address and a step-by-step Data Out guide, with a warning if Windows treats your network as Public. |

## Screenshots

<sub>All screenshots show synthetic telemetry, not a real drive.</sub>

| | |
| :---: | :---: |
| ![Performance](docs/screenshots/performance.png)<br>**Performance** | ![Laps](docs/screenshots/laps.png)<br>**Laps** |
| ![Sessions](docs/screenshots/sessions.png)<br>**Sessions** | ![Analysis](docs/screenshots/analysis.png)<br>**Analysis** |
| ![Map](docs/screenshots/map.png)<br>**Map** | ![Tuning](docs/screenshots/tuning.png)<br>**Tuning** |
| ![Settings](docs/screenshots/settings.png)<br>**Settings** | ![Dashboard](docs/screenshots/dashboard.png)<br>**Dashboard** |

## Set up the game

1. In the game open **Settings → HUD and Gameplay**.
2. Turn **Data Out** on.
3. Set **Data Out IP Address** to the address shown in the app.
4. Set **Data Out IP Port** to `5300`.

The game only sends packets while you are driving, so nothing shows up in menus or while paused.

**Xbox:** the console and the PC must be on the same network (wired or Wi-Fi). If you use a VPN on the PC, enable its "LAN access" option.

Full step-by-step guide with troubleshooting: [docs/data-out-setup.md](docs/data-out-setup.md).

## Build from source

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build ForzaTelemetry.slnx
dotnet test ForzaTelemetry.slnx
installer/build.ps1 -Version 0.1.0   # needs Inno Setup 6; writes the installer and the portable zip to dist/
```

Pushing a tag such as `v0.1.0` runs the release workflow, which builds both files and publishes them on a GitHub Release.

### Architecture

| Project | Role |
| --- | --- |
| `ForzaTelemetry.Core` | Pure telemetry model and packet parsing, no I/O. |
| `ForzaTelemetry.Udp` | UDP listener that feeds packets to Core. |
| `ForzaTelemetry.App` | The WPF application (MVVM), the composition root. |
| `ForzaTelemetry.Console` | Small command-line tool that prints live telemetry, useful for debugging. |

## License

Source-available under the [PolyForm Strict License 1.0.0](LICENSE). You may read and learn from the code and use the app for noncommercial purposes, but you may not modify, copy or redistribute it. This is not an open-source license.

## Credits

Built with .NET, WPF, CommunityToolkit.Mvvm and Serilog. See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for the third-party licenses.

Made by [Wladimir Hernandez](https://github.com/wladimirhernandez).
