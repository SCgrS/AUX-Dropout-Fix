@echo off
rem Builds AUX Dropout Fix with the C# compiler that ships with Windows. Output: bin\AUXDropoutFix.exe
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:"%~dp0bin\AUXDropoutFix.exe" "%~dp0src\*.cs"
exit /b %ERRORLEVEL%
