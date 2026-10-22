# HDMI activation and phone input methods

## Purpose

DX-Manager uses Android's simulated secondary display setting with the `hdmi`
suffix. ScrcpyDeX previously activated Samsung Wireless DeX through a loopback
RTSP connection. These are different firmware paths. The GUI now defaults to
the HDMI method to match the user's working DX-Manager setup while preserving
the upstream v1.1 interface and single EXE distribution.

This changes how the desktop is activated. It does not install, enable, disable,
or change the phone's default input method. Compatibility with Gboard, SwiftKey,
or other phone keyboards is a hardware test requirement, not an automated-test
result. UHID keyboard input remains enabled separately.

## Configuration

Close the application before editing `%APPDATA%\ScrcpyDeX\settings.json`.
Within its existing `device` section, set `"activationMode": "hdmi"` to use
the new method or `"activationMode": "wireless"` to retain the previous method.
Missing properties default to `hdmi`. There is no automatic fallback to Wireless
DeX because that would silently restore the input method behavior being tested.

The GUI's selected resolution and existing DPI preference determine the
simulated display. Firmware support is required; DX-Manager documents its
virtual display method primarily against One UI 8.x.

Android can show this simulated display as a floating preview on the phone. That
window can appear before the PC starts streaming, and its presence does not
prove that scrcpy has attached successfully. If activation fails, the application
removes its setting; Android dismisses the preview asynchronously. If it remains,
use the recovery instructions below.

Display detection merges Android's logical display blocks with independent
`DisplayInfo` records. It also runs the configured scrcpy executable with
`--list-displays`, using the same device selector and ADB executable. Baseline
IDs from either source are excluded when choosing a newly created display.
Failure diagnostics include the requested size and numeric before/after display
summaries. A timeout does not by itself mean the firmware lacks HDMI support.

The legacy PowerShell launcher still uses the previous Wireless DeX backend.
Use the released EXE for the new HDMI behavior.

## Ownership and cleanup

Stop DX-Manager and other simulated-display tools before starting. ScrcpyDeX
refuses to overwrite an existing `overlay_display_devices` value. It selects only
a newly created, non-primary display matching the requested dimensions and
refuses ambiguous matches. It never falls back to mirroring the phone display
after HDMI activation fails.

Stop, scrcpy-window exit, and application-window close remove the owned display.
Failed or canceled activation rolls back its setting. A failed cleanup retains
ownership so Stop/close can be retried. If another tool replaces the setting,
ScrcpyDeX leaves that tool's value intact. Closing the application waits for
cleanup rather than exiting while it is pending.

Force termination, USB disconnection, or a host crash can leave the simulated
display on the phone. Reconnect and close/stop the still-running application when
possible; otherwise open Developer options > Simulate secondary displays and
select None. This release does not install a phone-side recovery companion.

## Physical verification

1. Stop any other DeX session. Select the desired third-party keyboard as the
   phone's default input method before starting this application.
2. Start the released EXE. Confirm the diagnostic log reports HDMI activation.
3. Focus text fields in two Android apps. Check that the intended input method
   handles language switching, composition, candidate selection, and deletion.
4. Close the scrcpy window and check that the simulated display is removed.
   Repeat using Stop and by closing the main application window.
5. Report the phone model, One UI version, input method/version, and whether
   the same test succeeds in DX-Manager if behavior differs.

## References

- [DX-Manager virtual display implementation](https://github.com/maze-mei/DX-Manager/blob/main/DexManager/Services/VirtualDisplayService.cs)
- [DX-Manager compatibility notes](https://github.com/maze-mei/DX-Manager/blob/main/docs/FAQ_EN.md)
- [scrcpy keyboard modes](https://github.com/Genymobile/scrcpy/blob/master/doc/keyboard.md)
