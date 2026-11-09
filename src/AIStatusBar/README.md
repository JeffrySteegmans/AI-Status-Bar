# AI Status Bar (C# / WinUI 3)

Always-on-top strip showing your Claude Code and GitHub Copilot CLI sessions
(working / needs you / idle, elapsed time, tokens for Claude).

## Build
Needs the .NET 8+ SDK. Run `build.bat`; output goes to `dist\`.

## Install
Run `dist\install.bat`, then restart open Claude Code / Copilot CLI sessions.
Your `~/.claude/settings.json` is backed up before it is changed.
Extra arguments are passed to the bar (`install.bat --taskbar`).

## Uninstall
Run `uninstall.bat`.

## Options
`AIStatusBar.exe --taskbar | --corner tl|tc|tr|bl|bc|br | --no-adapt | --demo | --no-ui`

Right-click the bar for Reset position / Clear sessions / Quit. Left-drag moves it.
Debug: `AIStatusBar.exe --no-ui` prints every event received.

## Layout
- `AIStatusBar.exe` - WinUI 3 bar + collector (127.0.0.1:47600) + installer
- `AIStatusBarHook.exe` - tiny forwarder the agents run per hook event
