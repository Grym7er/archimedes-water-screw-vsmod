dotnet build -f net8.0

cd /home/dewet/Documents/projects/VintageStoryMods/archimedes_screw/bin/Debug/Mods/mod
zip -r mod.zip .
mv /home/dewet/Documents/projects/VintageStoryMods/archimedes_screw/bin/Debug/Mods/mod/mod.zip /home/dewet/.var/app/at.vintagestory.VintageStory/config/VintagestoryData/Mods/mod.zip 
