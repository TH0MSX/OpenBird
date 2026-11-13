@echo off
rem Starts OpenBird without a console window.
rem Python is taken from: .openbird-python (written by setup.ps1), then .venv, then PATH.
setlocal
set "ROOT=%~dp0"
set "PY="
if exist "%ROOT%.openbird-python" set /p PY=<"%ROOT%.openbird-python"
if defined PY goto run
if exist "%ROOT%.venv\Scripts\pythonw.exe" set "PY=%ROOT%.venv\Scripts\pythonw.exe"
if defined PY goto run
set "PY=pythonw.exe"
:run
start "" "%PY%" "%ROOT%main.py" gui
