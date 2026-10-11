@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul

rem ============================================================================
rem  TerminalMouse - one-shot mod test (build -> deploy -> launch -> kill -> logs)
rem
rem  Usage:   test.bat [wait_seconds]     default: 90
rem  Exit:    0 = ok, 1 = compile error (game NOT touched), 2 = deploy failed
rem
rem  What it does, in order:
rem    1. kills a running game (so the plugin dll is not locked)
rem    2. dotnet build -c Release        -> aborts on ANY compile error
rem    3. copies the built dll into BepInEx\plugins\TerminalMouse\
rem    4. deletes old logs, starts the game with its own -logFile
rem    5. waits, early-exits if the game dies by itself
rem    6. kills the game and prints mod lines + errors from both logs
rem ============================================================================

set "WAIT=%~1"
if "%WAIT%"=="" set "WAIT=90"

set "REPO=%~dp0"
set "PROJ=%REPO%TerminalMouse\TerminalMouse.csproj"
set "BUILT=%REPO%TerminalMouse\bin\Release\netstandard2.1\TerminalMouse.dll"
set "GAME=D:\steam\steamapps\common\Lethal Company"
set "EXE=%GAME%\Lethal Company.exe"
set "PLUGIN=%GAME%\BepInEx\plugins\TerminalMouse\TerminalMouse.dll"
set "BEPLOG=%GAME%\BepInEx\LogOutput.log"
set "OUT=%TEMP%\terminalmouse_test"
set "PLAYERLOG=%OUT%\Player.log"

if not exist "%OUT%" mkdir "%OUT%"

echo [TEST] step 1/6  closing running game, if any
taskkill /F /IM "Lethal Company.exe" /T >nul 2>&1
taskkill /F /IM "UnityCrashHandler64.exe" /T >nul 2>&1
ping -n 4 127.0.0.1 >nul

echo [TEST] step 2/6  dotnet build -c Release
dotnet build "%PROJ%" -c Release
if errorlevel 1 (
    echo [TEST] BUILD FAILED - game was NOT launched, plugin was NOT touched
    exit /b 1
)

echo [TEST] step 3/6  deploying dll into the game
fc /b "%BUILT%" "%PLUGIN%" >nul 2>&1
if errorlevel 1 (
    copy /Y "%BUILT%" "%PLUGIN%" >nul 2>&1
    fc /b "%BUILT%" "%PLUGIN%" >nul 2>&1
    if errorlevel 1 (
        rem file locked by something: rename it away, then write the new one
        ren "%PLUGIN%" "TerminalMouse.dll.old" >nul 2>&1
        copy /Y "%BUILT%" "%PLUGIN%" >nul 2>&1
    )
)
fc /b "%BUILT%" "%PLUGIN%" >nul 2>&1
if errorlevel 1 (
    echo [TEST] DEPLOY FAILED - dll in game folder is still the old one, locked?
    exit /b 2
)
for %%F in ("%BUILT%") do echo [TEST] deployed %%~nxF, %%~zF bytes

echo [TEST] step 4/6  clearing logs, launching game
del /F /Q "%BEPLOG%" >nul 2>&1
del /F /Q "%PLAYERLOG%" >nul 2>&1
pushd "%GAME%"
start "" "%EXE%" -logFile "%PLAYERLOG%"
popd

echo [TEST] step 5/6  running for max %WAIT% s
set /a ELAPSED=0
:loop
if !ELAPSED! GEQ %WAIT% goto wait_done
ping -n 6 127.0.0.1 >nul
set /a ELAPSED+=5
tasklist /FI "IMAGENAME eq Lethal Company.exe" 2>nul | find /i "Lethal Company.exe" >nul
if errorlevel 1 (
    echo [TEST] game exited on its own after !ELAPSED! s - probably a crash
    goto wait_done
)
goto loop
:wait_done

echo [TEST] step 6/6  stopping game
taskkill /F /IM "Lethal Company.exe" /T >nul 2>&1
taskkill /F /IM "UnityCrashHandler64.exe" /T >nul 2>&1
ping -n 4 127.0.0.1 >nul
copy /Y "%BEPLOG%" "%OUT%\LogOutput.last.log" >nul 2>&1

echo(
echo ================== MOD LINES - BepInEx\LogOutput.log ======================
findstr /i /c:"TerminalMouse" "%BEPLOG%"
echo ================== ERRORS - BepInEx\LogOutput.log ==========================
findstr /i /c:"Exception" /c:"[Error" /c:"error CS" /c:"Unhandled" "%BEPLOG%"
echo ================== MOD LINES - Player.log =================================
findstr /i /c:"TerminalMouse" "%PLAYERLOG%"
echo ================== Player.log - last 30 lines =============================
powershell -NoProfile -Command "if (Test-Path -LiteralPath '%PLAYERLOG%') { Get-Content -LiteralPath '%PLAYERLOG%' -Tail 30 } else { 'Player.log missing - game never wrote it' }"
echo ============================================================================
echo [TEST] full log copies: "%OUT%"
echo [TEST] done
endlocal
exit /b 0
