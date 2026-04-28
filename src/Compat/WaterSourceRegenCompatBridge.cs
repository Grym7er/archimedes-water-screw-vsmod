using System;
using HarmonyLib;
using Vintagestory.API.Server;

namespace ArchimedesScrew;

internal sealed class WaterSourceRegenCompatBridge : IDisposable
{
    private readonly ICoreServerAPI api;
    private readonly Harmony harmony;
    private bool isPatched;

    public WaterSourceRegenCompatBridge(ICoreServerAPI api)
    {
        this.api = api;
        harmony = new Harmony($"{ArchimedesScrewModSystem.ModId}.compat.source-regen");
    }

    public void EnsurePatched()
    {
        DisableWaterSourceRegenPatch.Api = api;

        if (isPatched)
        {
            return;
        }

        harmony.CreateClassProcessor(typeof(DisableWaterSourceRegenPatch)).Patch();
        if (!DisableWaterSourceRegenPatch.LastPrepareSucceeded)
        {
            ArchimedesScrewModSystem.LogVerboseOrNotification(
                api.Logger,
                "{0} [compat/source-regen] Harmony skipped patch (target method not resolved)",
                ArchimedesScrewModSystem.LogPrefix
            );
            return;
        }

        isPatched = true;
        ArchimedesScrewModSystem.LogVerboseOrNotification(api.Logger, "{0} [compat/source-regen] Water source regeneration disable active", ArchimedesScrewModSystem.LogPrefix);
    }

    public void Dispose()
    {
        Unpatch();
    }

    private void Unpatch()
    {
        if (!isPatched)
        {
            return;
        }

        harmony.UnpatchAll(harmony.Id);
        isPatched = false;
        ArchimedesScrewModSystem.LogVerboseOrNotification(api.Logger, "{0} [compat/source-regen] Patch unpatched", ArchimedesScrewModSystem.LogPrefix);
    }
}
