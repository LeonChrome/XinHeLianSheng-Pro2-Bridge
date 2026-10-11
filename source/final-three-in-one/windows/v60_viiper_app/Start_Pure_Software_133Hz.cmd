@echo off
setlocal
pushd "%~dp0"
"%~dp0XinHeLianShengPro2Bridge.exe" --pure-software-133
set "RESULT=%ERRORLEVEL%"
popd
if not "%RESULT%"=="0" (
  echo.
  echo Pure software 133Hz app failed to start. Exit code: %RESULT%
  pause
)
exit /b %RESULT%
