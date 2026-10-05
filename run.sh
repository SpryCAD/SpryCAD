#!/usr/bin/env bash
cd "$(dirname "$0")"
exec dotnet run --project src/FeatherCad.App -c Release -- "$@"
