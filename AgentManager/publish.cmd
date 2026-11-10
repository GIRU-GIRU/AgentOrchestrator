@echo off
rem Publishes Agent Manager to Publish\ (run Publish\AgentManager.exe from there). Close the app first.
dotnet publish "%~dp0AgentManager.App" -c Release -r win-x64 --self-contained false -o "%~dp0Publish"
