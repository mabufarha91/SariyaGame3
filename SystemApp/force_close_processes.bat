@echo off
echo Force closing processes that might be using Kinect Projects folder...
echo.

echo Closing any running KinectCalibrationWPF processes...
taskkill /f /im KinectCalibrationWPF.exe 2>nul
if %errorlevel% equ 0 (
    echo KinectCalibrationWPF.exe closed successfully
) else (
    echo No KinectCalibrationWPF.exe process found
)

echo.
echo Closing any running Visual Studio processes...
taskkill /f /im devenv.exe 2>nul
if %errorlevel% equ 0 (
    echo Visual Studio closed successfully
) else (
    echo No Visual Studio process found
)

echo.
echo Closing any running MSBuild processes...
taskkill /f /im msbuild.exe 2>nul
if %errorlevel% equ 0 (
    echo MSBuild closed successfully
) else (
    echo No MSBuild process found
)

echo.
echo Closing any running PowerShell processes...
taskkill /f /im powershell.exe 2>nul
if %errorlevel% equ 0 (
    echo PowerShell closed successfully
) else (
    echo No PowerShell process found
)

echo.
echo All processes closed. You should now be able to delete the folder.
pause
