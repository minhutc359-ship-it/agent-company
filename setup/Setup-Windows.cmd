@echo off
setlocal
cd /d "%~dp0"
py -3 -c "import sys; sys.exit(sys.version_info < (3, 10))" >nul 2>nul
if not errorlevel 1 goto run_py
python -c "import sys; sys.exit(sys.version_info < (3, 10))" >nul 2>nul
if not errorlevel 1 goto run_python
echo Installing Python 3.12 through Windows Package Manager...
winget install --id Python.Python.3.12 --exact --source winget
if exist "%LOCALAPPDATA%\Programs\Python\Python312\python.exe" goto run_installed
echo Install Python 3.12 from python.org or Microsoft App Installer if winget is missing, then reopen this file.
set "AICOS_SETUP_EXIT=1"
goto done
:run_py
py -3 setup-aicos.py %*
set "AICOS_SETUP_EXIT=%errorlevel%"
goto done
:run_python
python setup-aicos.py %*
set "AICOS_SETUP_EXIT=%errorlevel%"
goto done
:run_installed
"%LOCALAPPDATA%\Programs\Python\Python312\python.exe" setup-aicos.py %*
set "AICOS_SETUP_EXIT=%errorlevel%"
:done
if not defined CI pause
exit /b %AICOS_SETUP_EXIT%
