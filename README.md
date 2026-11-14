# OpenLine

A free, open-source, portable audio router and mixer for Windows, in the spirit of Logitech G MIXLINE. Native WinUI 3, no installer, no account.

Drag a wire from an input to a mix, set each input's level per mix, and press the headphones button to hear exactly what a mix sends.

## What it does

- **Inputs:** a microphone or line in, a single app (game, Spotify, Discord, browser), or all desktop audio. Desktop audio leaves out OpenLine itself, so a mix can't echo back into itself.
- **Mixes:** each mix plays to an output device, with its own level for every input (independent submixes) plus a master fader, mute, and meter.
- **Listen:** hear any mix on your headphones (choose the device in Settings).
- **Virtual microphone:** send a mix to **CABLE Input** and apps like OBS, Streamlabs, or Discord can pick **CABLE Output** as their microphone.
- **VB-Cable setup from inside the app:** downloads the official package from vb-audio.com, checks VB-Audio's code signature, and opens VB-Audio's own installer.
- **Keeps working:** reconnects when devices are unplugged or the Windows default changes, and re-attaches to an app when it restarts.
- **Portable:** settings live in `OpenLine.settings.json` next to the exe. Tray icon, close-to-tray, start with Windows (`--tray` starts hidden).

## Get it

Download a zip from Releases (or the Actions artifacts), unzip anywhere, run `OpenLine.exe`. Needs Windows 10 version 2004 or later (app and desktop capture use per-process loopback). x64 and ARM64 builds.

## Screenshot

<img width="1491" height="845" alt="image" src="https://github.com/user-attachments/assets/89f69aba-20f3-495b-bbe0-81ff88d2b040" />


## Build

Windows with the .NET 10 SDK:

```powershell
./build.ps1 -Arch x64     # -> dist/OpenLine-win-x64.zip
dotnet test tests/OpenLine.Core.Tests
```

Or open `OpenLine.sln` in Visual Studio, set the platform to x64, and run `OpenLine`.

## Good to know

- **One virtual mic with the free VB-Cable.** Only one mix can reach other apps as a microphone. Extra mixes can play to real devices, or to VB-Audio's paid A/B cables.
- **Discord using CABLE Output as its mic?** Add the apps you want individually instead of "All desktop audio". Otherwise your friends hear themselves.
- **VB-Cable is donationware** by VB-Audio, and its license doesn't allow bundling it in another installer. OpenLine never redistributes it: it fetches it from vb-audio.com and runs VB-Audio's setup. Please support them: https://vb-cable.com
- **Latency** is roughly 40 to 80 ms end to end (shared-mode WASAPI plus a small jitter buffer). Fine for streaming and calls; not meant for live instrument monitoring.

## Project layout

```
src/OpenLine.Core   Audio engine (NAudio 3, WASAPI), view models, settings, VB-Cable setup, tray
src/OpenLine        WinUI 3 app (Windows App SDK 2.6, unpackaged, self-contained)
tests/              Engine unit tests (ring buffer, mixing, ramps, clamping, meters)
```

## License

MIT. OpenLine is not affiliated with or endorsed by Logitech. MIXLINE is a trademark of Logitech.
