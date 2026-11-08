@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found. .NET Framework 4.x is required.
  pause
  exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:anycpu /out:"%~dp0..\bin\EbkPacker.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0EbkPacker.cs"
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo Built: %~dp0..\bin\EbkPacker.exe
pause