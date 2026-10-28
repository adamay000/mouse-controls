@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /codepage:65001 /out:MouseControls.exe /win32icon:app.ico /resource:app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll MouseControls.cs
