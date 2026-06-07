#!/usr/bin/env bash
set -euo pipefail


export VINTAGE_STORY=${VINTAGE_STORY_NET8:-$HOME/.local/share/vintagestory}
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mod_dir="$script_dir/bin/Debug/Mods/mod"
rm -rf "$mod_dir"
dotnet build -f net8.0 -c Debug
output_zip="$mod_dir/mod.zip"
mods_dir="${VINTAGE_STORY_MODS_DIR:-$HOME/.var/app/at.vintagestory.VintageStory/config/VintagestoryData/Mods}"
rm -rf "$mods_dir/mod.zip"
cd "$mod_dir"
zip -r mod.zip .
mv "$output_zip" "$mods_dir/mod.zip"
