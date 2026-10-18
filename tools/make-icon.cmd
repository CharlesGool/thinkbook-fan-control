@echo off
rem Regenerates res\app.ico from the glyph in src\FanIcon.cs. Optional argument: preview PNG path.
cd /d "%~dp0.."
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /codepage:65001 /out:"%TEMP%\IconGen.exe" /r:System.Drawing.dll tools\IconGen.cs src\FanIcon.cs || exit /b 1
"%TEMP%\IconGen.exe" res\app.ico %1
del "%TEMP%\IconGen.exe"
