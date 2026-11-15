# OpenLine

Free, portable audio router for Windows, inspired by Logitech G MIXLINE. No installer, no account.

<img width="1498" height="934" alt="OpenLine screenshot" src="https://github.com/user-attachments/assets/12a262c4-b23f-4ae5-a966-e6b626571a85" />

## Features

- **Inputs:** mic, single app, or all desktop audio (excludes OpenLine)
- **Mixes:** output device, per-input levels, master, mute, meter
- **Listen:** hear any mix on your headphones
- **Virtual mic:** send a mix to VB-Cable; one-click setup from vb-audio.com
- **Portable:** settings next to the exe; tray, start with Windows

## Download

[Releases](../../releases) → unzip → run `OpenLine.exe`. Windows 10 2004+, x64 or ARM64.

## Build

```powershell
./build.ps1 -Arch x64
dotnet test tests/OpenLine.Core.Tests
```

## Notes

- Free VB-Cable = one virtual mic.
- VB-Cable is VB-Audio donationware and isn't bundled. [Support them](https://vb-cable.com).
- Latency ~40–80 ms: fine for calls and streaming.
- `extras/raw-mic-extension`: browser extension that turns off voice filtering on chat sites.

## License

MIT. Not affiliated with Logitech. MIXLINE is a Logitech trademark.
