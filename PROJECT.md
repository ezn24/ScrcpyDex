# ScrcpyDeX keyboard and input method compatibility

## Source and requirements

This branch preserves the original upstream v1.1 release interface, using release
commit `8edab188e0ce439a03e5a3a10deb32714a9bac61`. That release commit
contains the embedded server deployment and UI shipped in the upstream single
EXE; it is newer than the upstream `v1.1` tag.

The keyboard fix adds `--keyboard=uhid` to the compiled client and script launcher.
The single EXE reuses upstream's embedded server resource and extraction logic.
Official scrcpy and ADB remain external prerequisites, as with upstream.

## DeX activation

The compiled GUI defaults to simulated HDMI activation using
`settings put global overlay_display_devices WIDTHxHEIGHT/DPI,hdmi`, the same
display creation method used by DX-Manager. This is intended to address the
different phone-side input method behavior observed with Wireless DeX; it is not
yet verified with Gboard or SwiftKey on physical hardware.

The existing interface is retained. The settings file adds
`device.activationMode`: `hdmi` (default) or `wireless` (previous loopback method).
See [the HDMI input method guide](docs/HDMI_INPUT_METHODS.md) for switching modes,
cleanup, compatibility, and physical testing.

## Build on Windows

Install the .NET 8 SDK, then run from the repository root:

```powershell
dotnet publish client/ScrcpyDex.WinUI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=1.1.3 -p:AssemblyVersion=1.1.3.0 -p:FileVersion=1.1.3.0 -o publish/single-exe
```

The distributable is `publish/single-exe/ScrcpyDeX.exe`; it embeds the server JAR.
No companion JAR, assets directory, or configuration template is needed.

## Verification and release

The existing `.github/workflows/ci.yml` builds the single EXE, copies only that
file to an isolated directory, and runs `--verify-bundle`. This checks the embedded
JAR against the resolved file and verifies the UHID keyboard argument without
opening the interface or contacting a phone. A passing build publishes `v1.1.3`
with `ScrcpyDeX.exe` and `SHA256SUMS.txt` as direct release assets.

The `fix/v1.1-uhid-keyboard` branch is retained for maintenance. No workflow step
deletes branches.

Run the device-independent HDMI lifecycle tests with:

```powershell
dotnet run --project tests/HdmiSession.Tests/HdmiSession.Tests.csproj
```

Hardware recognition still requires a connected Samsung phone. Restart the DeX
session, then check Settings > General management > Physical keyboard and test
typing and keyboard shortcuts.
