@echo off
setlocal
cd /d "%~dp0"

set "SRC=EbkPacker.cs"
set "OUT=EbkPacker.exe"
set "ICON="
if exist "app.ico" set "ICON=/win32icon:app.ico"

echo ============================================
echo   EBK Packer - Build Script
echo ============================================
echo.

if not exist "%SRC%" (
    echo [ERROR] Cannot find %SRC% in current folder.
    echo         Save your code as %SRC% next to this bat file.
    echo.
    pause
    exit /b 1
)

set "CSC="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not defined CSC (
    echo [ERROR] csc.exe not found. Need .NET Framework 4.x installed.
    echo.
    pause
    exit /b 1
)
echo Compiler: %CSC%
echo.

tasklist /fi "imagename eq %OUT%" 2>nul | find /i "%OUT%" >nul
if not errorlevel 1 (
    echo [WARN] %OUT% is running. Please close it first.
    echo.
    pause
    exit /b 1
)

echo Building...
echo.
"%CSC%" /nologo /target:winexe /optimize+ %ICON% /out:"%OUT%" ^
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    "%SRC%"

if errorlevel 1 (
    echo.
    echo [FAILED] See errors above.
    echo.
    pause
    exit /b 1
)

echo.
echo [OK] Created: %CD%\%OUT%
echo.
set "RUN="
set /p RUN=Run now? (Y/N) 
if /i "%RUN%"=="Y" start "" "%OUT%"
echo.
pause
endlocal
