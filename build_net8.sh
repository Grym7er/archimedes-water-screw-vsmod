#!/usr/bin/env bash
set -euo pipefail

dotnet build -f net8.0

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mod_dir="$script_dir/bin/Debug/Mods/mod"
output_zip="$mod_dir/mod.zip"
mods_dir="${VINTAGE_STORY_MODS_DIR:-$HOME/.var/app/at.vintagestory.VintageStory/config/VintagestoryData/Mods}"

cd "$mod_dir"
zip -r mod.zip .
mv "$output_zip" "$mods_dir/mod.zip"
