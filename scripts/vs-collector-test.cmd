@echo off
rem Starts Vintage Story with a .NET garbage-collector setting, for the stutter tests of
rem Session 111. Nothing in the game or mod install changes: the setting lives only in this
rem window's environment and in the game started from it. Starting the game the usual way
rem runs it exactly as before. The hitch recorder writes the setting in force to the client
rem log ("Garbage collector in this process: ..."), so every session says which one it was.
rem
rem   2  bigger allowance: a small collection after 128 MB of new objects instead of the
rem      default 48 MB on this PC (asking for more is capped at 128 MB by the runtime)
rem   3  all-cores collector (.NET Server GC, adaptive heap count): every core works on
rem      each collection, and collections run far less often
setlocal
echo.
echo  Vintage Story - garbage collector test
echo.
echo    1  normal (the game's default)
echo    2  bigger allowance (128 MB before each small collection)
echo    3  all-cores collector (Server GC)
echo.
choice /c 123 /n /m "  Press 1, 2 or 3: "
if errorlevel 3 goto allcores
if errorlevel 2 goto allowance
goto start

:allowance
set DOTNET_GCgen0size=0x8000000
goto start

:allcores
set DOTNET_gcServer=1
goto start

:start
start "" /d "%APPDATA%\Vintagestory" "%APPDATA%\Vintagestory\Vintagestory.exe"
endlocal
