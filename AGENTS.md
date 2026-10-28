# desk-switcher

Windows tray app showing the current virtual desktop number; left-click lists desktops (with top window) and switches via keyboard shortcuts.

## Build / run

- Compiler: .NET Framework 4.x `csc.exe` (no dotnet SDK installed).
- Build:
  `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /out:DeskSwitcher.exe DeskSwitcher.cs /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:Microsoft.CSharp.dll`
- Run: `.\DeskSwitcher.exe`
- `*.exe` is gitignored; do not commit build artifacts.

## Architecture notes

- Single source file: `DeskSwitcher.cs`.
- Desktop list/current index come from registry: `HKCU\...\Explorer\VirtualDesktops` (`VirtualDesktopIDs`, `CurrentVirtualDesktop`), 16 bytes per GUID.
- Window-to-desktop mapping uses COM `IVirtualDesktopManager.GetWindowDesktopId`.
- Switching uses `SendInput` with Ctrl+Win+Arrow, repeated for adjacent steps. `KEYBDINPUT` must be declared with `Size = 32` so `INPUT` is 40 bytes on x64; otherwise `SendInput` returns 0 and silently fails.
- Left-click shows a `ContextMenuStrip` at the cursor; right-click shows the same menu automatically.
- Top-window collection skips windows whose title contains "Desktop Switcher" or "DeskSwitcher".

## Verification

- No automated tests. Smoke test by running the exe and switching desktops from the tray menu.