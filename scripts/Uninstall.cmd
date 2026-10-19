@echo off
cd /d "%SystemRoot%"
setlocal DisableDelayedExpansion
set "QuickShotUninstallScript=%~dp0Uninstall.ps1"
rem End this batch context before deleting it; the parsed command block still runs.
(goto) 2>nul & (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%QuickShotUninstallScript%" %*
    if errorlevel 1 (
        echo QuickShot uninstall failed. Close the reported owner and retry.
        pause
        cmd.exe /d /c exit 1
    ) else (
        pause
        cmd.exe /d /c exit 0
    )
)

