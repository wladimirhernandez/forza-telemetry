# Data Out setup guide

Forza Telemetry listens for UDP packets that the game sends while you drive. This guide gets the two talking.

## What you need

- The app running on your PC.
- The game on the same PC, or on an Xbox on the same local network.
- The PC's local IP address. The app shows it in the top bar of every tab (with a button that copies it) and in the Settings tab, next to a three-step version of this guide.

## Steps

1. Start Forza Telemetry and press **Start** (or turn on **Start listening when the app opens** in Settings). Note the IP address and port it shows (default port `5300`). To use another port, change it in Settings and press **Apply**; a running listener moves to the new port by itself.
2. In the game open **Settings**, then **HUD and Gameplay**.
3. Scroll to the Data Out options and set:
   - **Data Out**: On
   - **Data Out IP Address**: the address shown in the app
   - **Data Out IP Port**: `5300`
4. Start driving. The app switches from "waiting for data" to **Receiving** within a second, and the top bar shows the packet rate.

The game does not send anything in menus, in photo mode or while paused. Always test while driving.

If the game and the app run on the same PC, use the PC's local network address shown in the app rather than a made-up one.

## Troubleshooting: no data arrives

Work through these in order.

1. **Are you actually driving?** Packets only flow during gameplay, not in menus.
2. **IP address.** It must be the PC's current address. Routers can change it after a reboot, so re-check the app and update the game.
3. **Port.** The port in the game and in the app must match (`5300` by default).
4. **Windows Firewall.** The installer adds an inbound rule named "Forza Telemetry (UDP 5300)" for the installed program on private networks. If you did not use the installer, or you moved the app, allow `ForzaTelemetry.App.exe` inbound on UDP 5300.
5. **Network marked Public.** By default the firewall rule only applies to **Private** networks, and Windows sometimes marks a home network as Public. The app checks this: when your network is Public, the Settings tab shows a **This PC's network is set to Public** warning with these same steps and a button to check again after you change it. To switch it: open Windows Settings, then **Network and internet**, then **Wi-Fi** (or **Ethernet**), open your connection's properties, and set **Network profile type** to **Private network**. Only do this on a network you trust. As a last resort, you can run the installer again and tick **Also allow on public networks (not recommended)**; that opens the port on untrusted networks too, so prefer the Private profile.
6. **VPN.** Many VPN clients block local traffic. Turn on the "LAN access" or "allow local network" option, or disconnect while you use the app.
7. **Xbox on another network.** The console and the PC must be on the same subnet (for example both `192.168.1.x`). Guest Wi-Fi, separate VLANs and some mesh "isolation" features break this. Wired or Wi-Fi both work if they are on the same network.
8. **Another program on the port.** Only one program can listen on a UDP port. Close other telemetry tools, or change the port in both the app and the game.

## Removing the firewall rule

Uninstalling the app removes the rule. To remove it by hand, run in an administrator terminal:

```powershell
netsh advfirewall firewall delete rule name="Forza Telemetry (UDP 5300)"
```
