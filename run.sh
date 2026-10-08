#!/usr/bin/env bash
cd "$(dirname "$0")"
exec dotnet run --project src/SpryCad.App -c Release -- "$@"
