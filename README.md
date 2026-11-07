# GunTester

A full-screen test screen for PC light guns on Windows. It reads each gun separately using Raw Input, the same way MAME does, so several guns can be tested at once.

Built for [OpenFIRE](https://github.com/TeamOpenFIRE) guns, and works with any gun that acts as an absolute-position mouse. Not affiliated with OpenFIRE.

## Features

- Calibration grid with targets at the centre, the middle of each edge and each corner
- One crosshair per gun, coloured by player (P1 red, P2 blue). It flashes on every trigger pull and shows **OFF-SCREEN** when the gun reports an edge position
- A panel per gun with:
  - button lamps (trigger, right, middle, back, forward)
  - any keys the gun sends
  - position
  - shot, off-screen and right-click counters
  - a "last input" timer
- Event log of guns connecting and disconnecting, including each gun's shot count when it dropped
- Recoil and rumble come from the gun's own firmware, exactly as in a game, so you can stress-test the solenoid without launching anything

Close the OpenFIRE App before testing. While it's open, the guns are in test mode and their recoil is off.

## Keys

The guns can't trigger these; they only work from a keyboard.

| Key | Action |
|-----|--------|
| Esc | Quit |
| C | Clear counters and log |
| G | Grid on/off |
| H | Hide help line |

## Building

No install needed. Windows already includes the .NET Framework C# compiler. From PowerShell, in the folder with `Program.cs` and `GunTester.ico`:

```
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:winexe /optimize+ /out:GunTester.exe /win32icon:GunTester.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll Program.cs
```

This compiler only supports C# 5, so keep any changes to C# 5 syntax. It prints a warning about the older language version; that's expected.

## License

MIT

# OFSolTool

A small Windows command-line tool that writes solenoid timing settings straight to an [OpenFIRE](https://github.com/TeamOpenFIRE) light gun over serial. Not affiliated with OpenFIRE.

## Why

The OpenFIRE App limits **Solenoid Autofire Wait Time** to 99 ms. Heavy arcade solenoids, such as original Namco Time Crisis guns, can't finish their return stroke in that time, so hold-to-repeat recoil turns into a weak buzz. The firmware itself accepts longer values; only the app's input box stops you entering them.

OFSolTool sends just the settings you give it, using the same docked-mode commit protocol as the app. Every other setting on the gun is left untouched.

## Usage

Close the OpenFIRE App first, because it holds the gun's COM port.

Interactive mode lists the OpenFIRE guns it finds, then asks for each value. Press Enter to leave a value unchanged:

```
.\OFSolTool.exe
```

Single command, one gun at a time:

```
.\OFSolTool.exe COM5 SolOn=70 SolOff=150 SolHold=220
```

| Setting | OpenFIRE App name |
|---------|-------------------|
| `SolOn` | Solenoid On length |
| `SolOff` | Solenoid Autofire wait time |
| `SolHold` | Solenoid Hold-to-autofire length |

Values are in ms, from 1 to 9999.

**Caveat:** if you open the OpenFIRE App and save any change to a gun, the app clamps `SolOff` back to 99 and writes 99 to the gun. A sign this has happened: "Save and Send Settings" lights up on a gun you haven't changed. Re-run OFSolTool after saving anything in the app.

## Building

No install needed. Windows already includes the .NET Framework C# compiler. From PowerShell, in the folder with `OFSolTool.cs`:

```
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:exe /optimize+ /out:OFSolTool.exe /r:System.dll /r:System.Core.dll /r:System.Management.dll OFSolTool.cs
```

## License

MIT
