@echo off
rem Double-click to build the .cipx package.
rem Uses "-ep bypass" so PowerShell is allowed to run the script.
setlocal
set "PS=powershell"
where pwsh >nul 2>nul && set "PS=pwsh"
%PS% -ep bypass -NoLogo -File "%~dp0Pack-Cipx.ps1" %*
pause
