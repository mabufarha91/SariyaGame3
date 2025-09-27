@echo off
echo Building KinectCalibrationWPF...
echo.

REM Try to find MSBuild in common locations
set MSBUILD_PATH=""

if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" (
    set MSBUILD_PATH="C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
) else if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe" (
    set MSBUILD_PATH="C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe"
) else if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe" (
    set MSBUILD_PATH="C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
) else (
    echo MSBuild not found in common locations!
    echo Please install Visual Studio or set MSBUILD_PATH environment variable
    pause
    exit /b 1
)

echo Using MSBuild: %MSBUILD_PATH%
echo.

REM Build the project
%MSBUILD_PATH% KinectCalibrationWPF.csproj /verbosity:normal /consoleloggerparameters:NoSummary

if %ERRORLEVEL% EQU 0 (
    echo.
    echo Build succeeded!
    echo Output: bin\Debug\KinectCalibrationWPF.exe
) else (
    echo.
    echo Build failed with error code %ERRORLEVEL%
    echo Check the output above for details
)

pause