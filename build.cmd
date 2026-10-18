@echo off
rem Builds ThinkBookFanControl.exe with the C# compiler that ships with .NET Framework 4.x (no SDK needed).
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist res\app.ico call tools\make-icon.cmd
if not exist bin mkdir bin
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 ^
  /out:bin\ThinkBookFanControl.exe /win32manifest:app.manifest /win32icon:res\app.ico /resource:res\app.ico,app.ico ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll ^
  src\*.cs
