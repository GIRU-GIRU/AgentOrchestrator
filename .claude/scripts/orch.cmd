@echo off
python "%~dp0orch\orch.py" %*
exit /b %ERRORLEVEL%
