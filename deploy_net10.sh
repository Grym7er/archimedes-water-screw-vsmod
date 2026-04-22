#!/usr/bin/env bash
set -euo pipefail

mod_dir="bin/Release/Mods/mod"
dotnet build -f net10.0 -c Release

# Create the zip file in the mod directory
cd "$mod_dir"
zip -r "thetruearchscrew-<version>.zip" .
