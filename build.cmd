@echo off
rem Builds ThinkBookFanControl.exe with the C# compiler that ships with .NET Framework 4.x (no SDK needed).
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist res\app.ico call tools\make-icon.cmd
if not exist bin mkdir bin
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 ^
  /out:bin\ThinkBookFanControl.exe /win32manifest:app.manifest /win32icon:res\app.ico /resource:res\app.ico,app.ico ^
  /resource:res\pawnio\IntelMSR.bin,pawnio.IntelMSR.bin /resource:res\pawnio\IntelMCHBAR.bin,pawnio.IntelMCHBAR.bin ^
  /resource:res\pawnio\LpcIO.bin,pawnio.LpcIO.bin /resource:res\pawnio\LpcACPIEC.bin,pawnio.LpcACPIEC.bin ^
  /resource:res\pawnio\PawnIO_setup.exe,pawnio.PawnIO_setup.exe ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.ServiceProcess.dll ^
  src\*.cs
if errorlevel 1 exit /b 1
rem tbfc.exe: command-line client for the control port (no admin manifest, so it never asks for UAC).
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /utf8output /codepage:65001 /out:bin\tbfc.exe /r:System.dll cli\Tbfc.cs
