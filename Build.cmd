@echo off
rem Builds AUX Dropout Fix with the C# compiler that ships with Windows. Output: bin\AUXDropoutFix.exe
rem 32-bit preferred: the background process needs about 4 MB less memory than as a 64-bit process.
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu32bitpreferred /codepage:65001 /out:"%~dp0bin\AUXDropoutFix.exe" "%~dp0src\*.cs"
exit /b %ERRORLEVEL%
