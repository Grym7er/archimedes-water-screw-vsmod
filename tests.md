# Archimedes Screw Mod - Source Model Test Plan

This test plan reflects the simplified source-only logic:

- each functional screw maintains exactly one Archimedes source at its output
- vanilla liquid propagation handles downstream flow
- extra vanilla source blocks placed into Archimedes water convert to the connected Archimedes family
- if support is lost, the mod deletes unsupported Archimedes source nodes and vanilla flow logic dries up the rest

## Default Config

| Setting | Value |
|---------|-------|
| fastTickMs | 250 |
| idleTickMs | 2000 |
| maxBlocksPerStep | 16 |
| maxScrewLength | 32 |
| minimumNetworkSpeed | 0.001 |

## Prerequisites

- Vintage Story 1.21.6 with the mod installed from `bin/Debug/Mods/mod/`
- a creative-mode test world
- a working mechanical power source
- server log visible for `[archimedes_screw]` messages
- run `/archscrew purge` between unrelated tests

---

## Placement And Assembly

### T01 - Intake In Still Water

| Field | Value |
|-------|-------|
| **Setup** | Place the intake inside still fresh water. |
| **Expected** | Placement succeeds. |
| **Result** | |

### T02 - Intake In Flowing Water

| Field | Value |
|-------|-------|
| **Setup** | Place the intake inside flowing vanilla water. |
| **Expected** | Placement succeeds. |
| **Result** | |

### T03 - Intake Rejected Outside Water

| Field | Value |
|-------|-------|
| **Setup** | Try placing the intake in air or against dry blocks. |
| **Expected** | Placement fails with the water requirement message. |
| **Result** | |

### T04 - Rotation And Models

| Field | Value |
|-------|-------|
| **Setup** | Place intakes and outlets from each cardinal direction. |
| **Expected** | Intake/outlet orientation is correct and the outlet model is upside down. |
| **Result** | |

### T05 - Empty-Hand Status Check

| Field | Value |
|-------|-------|
| **Setup** | Right-click a complete powered assembly, then an incomplete or unpowered assembly, with an empty hand. |
| **Expected** | The status message correctly reports functional vs non-functional state and explains problems. |
| **Result** | |

---

## Single-Source Behavior

### T06 - One Output Source Only

| Field | Value |
|-------|-------|
| **Setup** | Build a valid powered assembly with open space at the output. |
| **Expected** | The screw maintains one Archimedes source block at the output position. Downstream flow is produced by vanilla liquid behavior rather than by the mod spawning extra source nodes automatically. |
| **Result** |  Fail: single source is made, but vanilla flow is not triggered.|

### T07 - Output Family Matches Intake

| Field | Value |
|-------|-------|
| **Setup** | Build separate powered assemblies in fresh water, saltwater, and boiling water. |
| **Expected** | The output source family matches the intake family. |
| **Result** | |

### T08 - Open-Air Output Falls Naturally

| Field | Value |
|-------|-------|
| **Setup** | Build a powered assembly that outputs into open air. |
| **Expected** | The output source produces a natural downward fall using the Archimedes liquid family, and once supported it can spread normally. |
| **Result** | |

### T09 - Stable Idle Logging

| Field | Value |
|-------|-------|
| **Setup** | Let a powered assembly run for a while after the output flow stabilizes. |
| **Expected** | The log should not spam the exact same unchanged source summary line over and over. |
| **Result** | |

---

## Player-Placed Source Conversion

### T10 - Place Vanilla Source Inside Archimedes Flow

| Field | Value |
|-------|-------|
| **Setup** | Let an active Archimedes water flow form, then place a vanilla fresh-water source block into that flow. |
| **Expected** | The placed source is converted into the connected Archimedes family instead of remaining vanilla water. |
| **Result** | |

### T11 - Conversion Preserves Connected Family

| Field | Value |
|-------|-------|
| **Setup** | Repeat T10 in saltwater and boiling-water Archimedes flows. |
| **Expected** | The placed source converts to the matching connected Archimedes family. |
| **Result** | |

### T12 - Converted Source Stays Supported

| Field | Value |
|-------|-------|
| **Setup** | After T10/T11, keep the screw powered and observe the newly converted source. |
| **Expected** | The converted source is adopted by the connected active screw network and is not immediately deleted while support remains. |
| **Result** | |

---

## Disconnect And Cleanup

### T13 - Power Loss Removes Sources

| Field | Value |
|-------|-------|
| **Setup** | Create a flow with one or more Archimedes source nodes, then remove mechanical power. |
| **Expected** | The mod starts removing Archimedes source nodes from the farthest positions first. Vanilla flow then dries up naturally. |
| **Result** | |

### T14 - Screw Break Removes Sources

| Field | Value |
|-------|-------|
| **Setup** | Break a screw block in a running assembly. |
| **Expected** | The assembly becomes non-functional and connected Archimedes source nodes begin disappearing from the outside inward. |
| **Result** | |

### T15 - Interrupt Flow Mid-Channel

| Field | Value |
|-------|-------|
| **Setup** | Let the flow extend through a channel, then place a solid block between source-supported sections. |
| **Expected** | Only the disconnected Archimedes source nodes are deleted. Vanilla water logic then removes the unsupported downstream flowing water. |
| **Result** | |

### T16 - Remove Output Source Manually

| Field | Value |
|-------|-------|
| **Setup** | Break the Archimedes source block directly at the output while the screw is still powered. |
| **Expected** | The screw recreates its one output source. Any disconnected Archimedes source nodes downstream are cleaned up according to connectivity. |
| **Result** | |

### T17 - Flowing Water Dries After Source Deletion

| Field | Value |
|-------|-------|
| **Setup** | Create a visible falling or flowing Archimedes stream, then remove the supporting Archimedes source nodes by depowering or interrupting the network. |
| **Expected** | The remaining flowing/falling water dries up via vanilla propagation after the sources are removed. |
| **Result** | |

---

## Multiple Screws

### T18 - Any Connected Active Screw Can Support Sources

| Field | Value |
|-------|-------|
| **Setup** | Build two powered screws feeding the same connected Archimedes flow. Add one or more extra converted source blocks inside that connected flow. |
| **Expected** | The connected source nodes remain valid while at least one connected screw is still active. |
| **Result** | |

### T19 - One Screw Off, One Screw Still On

| Field | Value |
|-------|-------|
| **Setup** | Continue from T18 and depower only one of the two screws. |
| **Expected** | Source nodes that remain connected to the still-active screw stay alive. The body should not collapse simply because one supporting screw turned off. |
| **Result** | |

### T20 - All Connected Screws Off

| Field | Value |
|-------|-------|
| **Setup** | Continue from T19 and depower the remaining connected screw as well. |
| **Expected** | The connected Archimedes source nodes are removed from the farthest positions first, and the remaining flow dries up. |
| **Result** | |

---

## Persistence And Commands

### T21 - Save And Reload

| Field | Value |
|-------|-------|
| **Setup** | Save and reload a world containing active Archimedes source nodes and flowing water. |
| **Expected** | Tracked Archimedes source nodes restore correctly and the system resumes normal support/cleanup behavior after reload. |
| **Result** | |

### T22 - Purge Commands

| Field | Value |
|-------|-------|
| **Setup** | Run `/archscrew purge`, `/archscrew purgewater`, and `/archscrew purgescrews` in separate test setups. |
| **Expected** | The commands remove the expected screw blocks and/or Archimedes water. |
| **Result** | |

### T23 - No Crash Under Normal Use

| Field | Value |
|-------|-------|
| **Setup** | Play with the mod for several minutes: place assemblies, power/unpower, place extra sources, break channels, and save/reload. |
| **Expected** | No crashes and no repeated tick exceptions in the logs. |
| **Result** | |

---

## Notes

- This plan intentionally does not test the old pooled-volume or merged-budget mechanics because those mechanics were removed.
- For cleanup tests, focus on whether source-node deletion is correct; flowing-water disappearance may take additional vanilla liquid-update ticks.
