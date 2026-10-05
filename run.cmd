@echo off
dotnet run --project "%~dp0src\FeatherCad.App" -c Release -- %*
