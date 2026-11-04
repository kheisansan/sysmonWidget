@echo off
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /platform:anycpu ^
  /out:SysmonWidget.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Runtime.Serialization.dll /r:System.Security.dll ^
  SysmonWidget.cs GmailClient.cs
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)
echo Build succeeded: %~dp0SysmonWidget.exe
