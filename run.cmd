@echo off
dotnet run --project "%~dp0src\SpryCad.App" -c Release -- %*
