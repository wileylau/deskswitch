# DeskSwitcher

A lightweight Windows tray app for virtual desktops.

## Features

- Tray icon shows the current desktop number (or first letter of a renamed desktop).
- Left-click opens a popup listing each desktop with its top window title.
- Click a desktop to switch to it.
- Right-click / outside-click dismisses the popup.
- "+ New desktop" and per-desktop delete (×).
- Double-click a desktop name to rename it inline.
- Respects Windows accent color and light/dark theme.

## Build

Requires the .NET Framework 4.x C# compiler (no SDK needed):

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /out:DeskSwitcher.exe DeskSwitcher.cs /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:Microsoft.CSharp.dll
```

## Run

```
.\DeskSwitcher.exe
```

`DeskSwitcher.exe` is gitignored; build it locally.

## Notes

- Single instance only; launching again prompts to kill the existing instance.
- Switching uses `Ctrl+Win+Arrow`; deleting uses `Ctrl+Win+F4`.
- No automated tests.