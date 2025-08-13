@echo off
echo Building Kinect Calibration WPF Application...
echo.

REM Try to find MSBuild
set MSBUILD_PATH=""

REM Check for Visual Studio 2022
if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" (
    set MSBUILD_PATH="C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    goto :build
)

REM Check for Visual Studio 2019
if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe" (
    set MSBUILD_PATH="C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
    goto :build
)

REM Check for Visual Studio 2017
if exist "C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe" (
    set MSBUILD_PATH="C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe"
    goto :build
)

REM Check for .NET Framework MSBuild
if exist "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" (
    set MSBUILD_PATH="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
    goto :build
)

echo ERROR: MSBuild not found!
echo Please install Visual Studio or .NET Framework SDK
echo.
pause
exit /b 1

:build
echo Using MSBuild: %MSBUILD_PATH%
echo.

%MSBUILD_PATH% KinectCalibrationWPF.csproj /p:Configuration=Debug /p:Platform=x64 /verbosity:minimal

if %ERRORLEVEL% EQU 0 (
    echo.
    echo BUILD SUCCESSFUL!
    echo.
    echo The application has been built successfully.
    echo You can now run the application from the bin\Debug folder.
    echo.
    echo Note: If you don't have Kinect hardware, the application will run in test mode.
    echo.
) else (
    echo.
    echo BUILD FAILED!
    echo.
    echo Common issues:
    echo 1. Kinect SDK not installed - the app will work in test mode
    echo 2. .NET Framework 4.8 not installed
    echo 3. Visual Studio build tools not installed
    echo.
)

pause
