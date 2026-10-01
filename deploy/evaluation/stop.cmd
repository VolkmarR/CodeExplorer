@echo off
rem Stops the CodeExplorer evaluation server, whether start.cmd or Claude Desktop's proxy started it.
rem See README.md beside this file. The server is found by the path of its executable, this folder's
rem app\CodeExplorer.exe, so a CodeExplorer installed anywhere else is left alone, and so are the
rem proxies Claude Desktop runs, which are CodeExplorer.McpProxy.exe. While Claude Desktop is open,
rem its next question starts the server again; quit Claude Desktop first to keep it stopped.
setlocal
set "CODEEXPLORER_EXE=%~dp0app\CodeExplorer.exe"
powershell -NoProfile -Command "$exe = [IO.Path]::GetFullPath($env:CODEEXPLORER_EXE); $found = @(Get-CimInstance Win32_Process -Filter \"Name = 'CodeExplorer.exe'\" | Where-Object { $_.ExecutablePath -eq $exe }); if ($found.Count -eq 0) { Write-Host 'CodeExplorer is not running.'; exit 0 }; foreach ($p in $found) { Stop-Process -Id $p.ProcessId -Force; Write-Host ('Stopped CodeExplorer, process ' + $p.ProcessId + '.') }"
exit /b %errorlevel%
