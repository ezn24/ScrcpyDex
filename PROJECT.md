# ScrcpyDeX v1.1 physical keyboard fix

## Scope

This branch is based on the published upstream `v1.1` tag. Unlike the newer
Avalonia client on `main`, this release has no keyboard mode option. Both its
compiled WPF client and PowerShell launcher omit the keyboard argument, so scrcpy
uses SDK input: typing works without registering a physical keyboard on Android.

Both launch paths now pass `--keyboard=uhid`, matching DX-Manager's `-K` option.
The mouse setting remains independent. The change uses scrcpy's existing UHID
implementation and does not modify the Android server or Samsung settings.

## Build on Windows

Install the .NET 8 SDK and restore packages from NuGet.org. From the repository
root:

```powershell
dotnet restore client/ScrcpyDex.WinUI.csproj --source https://api.nuget.org/v3/index.json
dotnet publish client/ScrcpyDex.WinUI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/keyboard-fix
```

Keep `server/scrcpydex-server.jar` next to the executable, or in a `server`
subdirectory next to it. Install official scrcpy 2.4 or newer and ADB; the
application searches its directory and PATH as well as supported installation
locations. Use the existing v1.1 installation's runtime files if available.

## Verification

The existing `--self-test` entry point checks that generated arguments contain
`--keyboard=uhid`. It also runs the project's broader internal checks.

For hardware validation, close the previous session and start this build. On the
phone, open Settings > General management > Physical keyboard and check for the
scrcpy keyboard. Configure its layout and Android input method there. Test text,
Shift, Ctrl, and the DeX desktop shortcuts. Physical device recognition has not
been verified by an automated desktop build; it requires a connected phone.

## References

- [Upstream v1.1](https://github.com/Aureliano021/ScrcpyDex/tree/v1.1)
- [scrcpy keyboard documentation](https://github.com/Genymobile/scrcpy/blob/master/doc/keyboard.md)
