@echo off
rem Starts the CodeExplorer evaluation server in a window of its own and opens the web UI.
rem See README.md beside this file. The address is read from app\appsettings.Evaluation.json, the
rem same file the server and CodeExplorer.McpProxy read, so a port changed there is changed for all
rem three. If the server is already running, which it is once Claude Desktop has used it, this only
rem opens the browser.
setlocal
cd /d "%~dp0app" || exit /b 1

set "URL="
for /f "usebackq delims=" %%u in (`powershell -NoProfile -Command "((Get-Content -Raw 'appsettings.Evaluation.json' | ConvertFrom-Json).Urls -split ';')[0].Trim()"`) do set "URL=%%u"
if not defined URL set "URL=http://127.0.0.1:5000"

call :answers
if not errorlevel 1 goto open

echo Starting CodeExplorer at %URL% ...
rem The working directory is the app folder, as the proxy sets it: the relative paths in the settings
rem file and the key ring that protects stored tokens both follow it.
start "CodeExplorer server - run stop.cmd to stop it" CodeExplorer.exe --environment Evaluation

set /a tries=0
:wait
call :answers
if not errorlevel 1 goto open
set /a tries+=1
if %tries% geq 60 (
  echo CodeExplorer did not answer within a minute. The server window says why.
  pause
  exit /b 1
)
rem One second, without timeout.exe, which refuses to run when input is redirected.
ping -n 2 127.0.0.1 >nul
goto wait

:open
start "" "%URL%/"
exit /b 0

:answers
powershell -NoProfile -Command "try { $null = Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 '%URL%/api/projects'; exit 0 } catch { exit 1 }"
exit /b %errorlevel%
