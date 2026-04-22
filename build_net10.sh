#!/usr/bin/env bash
set -euo pipefail



script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mod_dir="$script_dir/bin/Debug/Mods/mod"
rm -rf "$mod_dir"
dotnet build -f net10.0
output_zip="$mod_dir/mod.zip"
mods_dir="${VINTAGE_STORY_MODS_DIR:-$HOME/.config/VintagestoryData/Mods}"
rm -rf "$mods_dir/mod.zip"
cd "$mod_dir"
zip -r mod.zip .
mv "$output_zip" "$mods_dir/mod.zip"
