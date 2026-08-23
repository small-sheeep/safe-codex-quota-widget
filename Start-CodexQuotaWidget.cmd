@echo off
setlocal
set "APP=%~dp0bin\SafeCodexQuotaWidget.exe"
if not exist "%APP%" (
  echo Widget has not been built. Run Build.ps1 first.
  pause
  exit /b 1
)
start "" "%APP%"
