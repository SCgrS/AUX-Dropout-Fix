# AUX Dropout Fix

### [⬇ Download for Windows (AUXDropoutFix.exe)](https://github.com/SCgrS/AUX-Dropout-Fix/releases/latest/download/AUXDropoutFix.exe)

[Türkçe](README.tr.md)

A small **Windows background tool** that stops portable speakers from cutting out over the AUX cable when your PC volume is low.

Some speakers mute themselves when the signal on their 3.5 mm input gets weak. At low PC volume, quiet parts of songs and videos drop out, so you end up keeping the PC at 100% and adjusting the speaker by hand. AUX Dropout Fix sends a constant, inaudible 20 Hz signal to the AUX output, so the speaker never thinks the input is silent.

Built for and tested on the **JBL Charge 4**, where it is confirmed to work. It may also help other speakers that mute their AUX input the same way.

## Installation

Requirements: Windows 10 or 11. Nothing else, it runs on the .NET Framework 4 that ships with Windows.

1. Download `AUXDropoutFix.exe` from the link above.
2. Put it in a folder of your choice, or build it yourself with `Build.cmd`, then run `tools\install.ps1` in PowerShell. The program is copied to `%LOCALAPPDATA%\Programs\AUXDropoutFix`, added to Windows startup and started.
3. If Windows SmartScreen warns you, click "More info" › "Run anyway". The warning appears because the program is not signed.

## Highlights

- **Inaudible:** The signal is 20 Hz at -40 dB on the output.
- **Independent of Windows volume:** When you turn the Windows volume down, the signal is boosted by the same amount, so the level reaching the speaker stays the same.
- **Only while sound plays:** If no other program is playing on that output, the signal fades out after 15 seconds. The speaker can still go idle and turn itself off.
- **No window:** Runs quietly in the background and starts with Windows.

## What it does

### The signal

The signal goes to the first active output of the "High Definition Audio Device" adapter that is not headphones, usually the line or speaker jack. It keeps going there even when the default output is another device (for example Bluetooth headphones), but stays off as long as nothing plays on that output.

Below about 7% Windows volume, the signal cannot be boosted any further and starts to get weaker. On the JBL Charge 4 there were no dropouts at 5%. Below that, the speaker may mute again.

### Settings

You can add `name=value` lines to `%LOCALAPPDATA%\AUXDropoutFix\settings.txt`. They are read when the program starts.

| Name | Default | Meaning |
| --- | --- | --- |
| `output` | (first output of "High Definition Audio Device") | The Windows name of the output, for example `Speakers (High Definition Audio Device)` |
| `frequency` | `20` | Signal frequency (Hz) |
| `targetdb` | `-40` | Signal level on the output (dB) |
| `maxdb` | `-3` | How far the signal can be boosted at low Windows volume (dB) |
| `holdseconds` | `15` | How long the signal stays on after sound stops (seconds) |

### Note

If the speaker is connected to the same PC with both the AUX cable and a USB charging cable, you may hear a hum. Since the speaker no longer mutes itself, the hum becomes audible. Charge the speaker from a wall charger instead of the PC.

### How it works

The program opens its own audio stream on the output with WASAPI (shared mode, event driven, 20 ms buffer). Every 10 ms it reads the Windows volume and sets the signal's amplitude to match. A separate thread checks the audio sessions on that output every half second and turns the signal on while a session from another program is active. `tools\sessions.ps1` shows which programs are playing sound right now.

## Updating

The program does not update itself. To install a new version, download it and run `tools\install.ps1` again. The running copy is stopped and the new one is started.

## Uninstalling

Run `tools\uninstall.ps1` in PowerShell. The program is stopped, removed from Windows startup, and its program folder and its settings and log folder are deleted.

## Your data

The program does not connect to the internet and sends nothing. It only writes start, stop and error lines to `%LOCALAPPDATA%\AUXDropoutFix\log.txt`. The file starts over once it passes 200 KB.

## Third-party components

None. The program only uses components that ship with Windows.

## Thanks

The [Reverse-engineering JBL flip 4](https://hackaday.io/project/162337-reverse-engineering-jbl-flip-4) project on Hackaday showed that these speakers mute the AUX input in hardware, based on signal level. The idea of a constant low-frequency signal also comes from there and from [this iFixit discussion](https://www.ifixit.com/Answers/View/405479/Why+playing+via+AUX+cut's+on+quiet+passages+in+the+songs-movies).

---

Questions and feedback: [x.com/CgrShn](https://x.com/CgrShn)

License: AGPL-3.0. See the [LICENSE](LICENSE) file for details.
