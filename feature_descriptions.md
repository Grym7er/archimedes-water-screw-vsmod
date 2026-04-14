# Pipes
## Overview
Add lead pipes to create water transporation networks and multiblock water containment structures:
1. Simplified network pressure (not in this update)
    - Perform a simplified conversion/calculation to check how we can convert wind power to a rough pressure
    - Pressure needs to be maintained to move water up.
    - You need X pressure to move water upwards Y blocks
    - Tie this in with the current water transport system.
    - Let pressure function as a currency: Start at pressure X, every pipe block away from the pipe the pressure decays, either linearly or whatever.
    - Gravity creates pressure based on height above sea level.
2. Lead pipes:
    - Create pipe network
    - Pipes can transport water at all angles.
    - Relies on pressure to move water
    - taps: can be used to fill buckets and other containers by placing them underneath the tap or something 
3. Reservoir:
    - Multiblock Structure
    - Volume is as in IRL/10, i.e 100L per 1 cubic meter (otherwise OP)
    - Can be fed with pipes
    - Can be used as a water battery for when wind is low

# Next update:
1. Reservoirs
2. Taps

## Reservoirs:
1. Multiblock structure.
2. User can construct a dynamically sized structure, as long as the reservoir container is valid:
    - specific block types
    - maximum size
    - Requires output valve. 
3. Fills up with archimedes source blocks

### Output Valve:
Allows gravity-fed pipes. Connect to a reservoir and open, works like an archimedes screw, but displaces water, i.e takes from input and moves to output, Uses relays, coupled to input sources


# Implementation plan:
1. First, we need to re-do relay creation logic to work as follows:
    - in stead of making it at flowing-level-1, make flowing-level-6 the relay candidates.
    - prioritize relay creation nearest-first from the controller seed (instead of farthest-first).
1. Start with the "archimedes source blocks fills up spaces with relays until full:
    - While there is relay budget
    - locked at idle-speed:
        - otherwise we will fill up without waiting to check whether a flow-relay can be created
    - if no viable flow-relays (purple)
    - find the end of the water stream, i.e move along water away from controller until cannot move farther
    - find an archimedes source closeby
    - create a relay next to that source
    - repeat from (1)
