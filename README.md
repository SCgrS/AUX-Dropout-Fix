# AUX Dropout Fix

[Türkçe](README.tr.md)

A small **Windows background tool** that stops portable speakers from cutting out over the AUX cable when your PC volume is low.

Some speakers mute themselves when the signal on their 3.5 mm input gets weak. At low PC volume, quiet parts of songs and videos drop out, so you end up keeping the PC at 100% and adjusting the speaker by hand. AUX Dropout Fix sends a constant, inaudible 20 Hz signal to the AUX output, so the speaker never thinks the input is silent.

Built for and tested on the **JBL Charge 4**, where it is confirmed to work. It may also help other speakers that mute their AUX input the same way.

## Installation

Requirements: Windows 10 or 11. Nothing else is needed. The program runs on the .NET Framework 4 that ships with Windows.

There is no ready-made download yet. The program is built from its source code with the C# compiler that ships with Windows.

1. On this page, choose **Code › Download ZIP** and extract the ZIP file.
2. Double-click `Build.cmd`. It creates `bin\AUXDropoutFix.exe`.
3. Double-click `bin\AUXDropoutFix.exe`. It installs itself for your user (no administrator rights needed), adds itself to Windows startup and to **Settings › Apps › Installed apps**, and starts running in the background. A message confirms that it is installed.
4. If Windows SmartScreen warns you, click **More info** › **Run anyway**. The warning appears because the files are not signed.

## Highlights

- **Inaudible:** The signal is 20 Hz at -40 dB on the output.
- **Independent of Windows volume:** When you turn the Windows volume down, the signal is boosted by the same amount, so the level reaching the speaker stays the same.
- **Only while sound plays:** If no other program is playing sound on that output, the signal fades out after 15 seconds and the program stops its audio stream. The speaker can still go idle and turn itself off, and the PC can go to sleep.
- **No window:** Runs quietly in the background and starts with Windows.

## What it does

### The signal

The signal goes to the first active speaker or line output of the "High Definition Audio Device" adapter (not headphones or HDMI), usually the line or speaker jack. It keeps going there even when the default output is another device (for example Bluetooth headphones), but stays off as long as nothing plays on that output.

Below about 7% Windows volume, the signal cannot be boosted any further and starts to get weaker. On the JBL Charge 4 there were no dropouts at 5%. Below that, the speaker may mute again.

While another program plays loudly, the signal steps back so that the two together do not go over full scale and crackle. The speaker does not mute during loud passages anyway.

### Settings

You can add `name=value` lines to `%LOCALAPPDATA%\AUXDropoutFix\settings.txt`. They are read when the program starts. A value outside its range is ignored and noted in the log.

| Name | Default | Range | Meaning |
| --- | --- | --- | --- |
| `output` | (first speaker output of "High Definition Audio Device") | | The Windows name of the output, for example `Speakers (High Definition Audio Device)` |
| `frequency` | `20` | 10 to 30 | Signal frequency (Hz) |
| `targetdb` | `-40` | -60 to -30 | Signal level on the output (dB) |
| `maxdb` | `-3` | -60 to -3 | How far the signal can be boosted at low Windows volume (dB) |
| `holdseconds` | `15` | 0 to 3600 | How long the signal stays on after sound stops (seconds) |

### Note

If the speaker is connected to the same PC with both the AUX cable and a USB charging cable, you may hear a hum. Since the speaker no longer mutes itself, the hum becomes audible. Charge the speaker from a wall charger instead of the PC.

### How it works

The program opens its own audio stream on the output with WASAPI (shared mode, event driven, 20 ms buffer). Every half second it checks the audio sessions on that output. A session from another program counts as playing only while it actually produces sound, so a program that keeps a silent stream open does not turn the signal on. The stream is started when sound begins and stopped once the signal has faded out. While it runs, the program reads the Windows volume and the other programs' peak levels every 10 ms and sets the signal's amplitude to match. It opts out of the Windows feature that lowers other sounds during calls, and it runs as a 32-bit process, which needs less memory. `tools\sessions.ps1` shows which programs are playing sound right now.

## Updating

The program does not update itself. To install a new version, download and build it again as described above, then double-click the new `bin\AUXDropoutFix.exe`. The running copy is stopped and replaced.

## Privacy

The program does not connect to the internet, sends nothing and has no telemetry. It reads its settings from `%LOCALAPPDATA%\AUXDropoutFix\settings.txt` and only writes start, stop, signal on and off, and error lines to `%LOCALAPPDATA%\AUXDropoutFix\log.txt`. Once the log passes 200 KB, it is renamed to `log.old.txt` and a new one is started.

Uninstalling it from **Settings › Apps › Installed apps** stops the program, removes it from Windows startup, and deletes its program folder and its settings and log folder.

## Third-party components

None. The program only uses components that ship with Windows.

## Thanks

The [Reverse-engineering JBL flip 4](https://hackaday.io/project/162337-reverse-engineering-jbl-flip-4) project on Hackaday showed that these speakers mute the AUX input in hardware, based on signal level. The idea of a constant low-frequency signal also comes from there and from [this iFixit discussion](https://www.ifixit.com/Answers/View/405479/Why+playing+via+AUX+cut's+on+quiet+passages+in+the+songs-movies).

---

Questions and feedback: [x.com/CgrShn](https://x.com/CgrShn)

License: AGPL-3.0. See the [LICENSE](LICENSE) file for details.
