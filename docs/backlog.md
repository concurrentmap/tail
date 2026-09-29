# Backlog / future actions

## To verify in-game (implemented and compiling, not yet seen working in a build)
- Route light columns over each of the Mark's stops (red → green when visited; next one pulses).
- Lap-map zoom: street labels keep their size when zoomed (double counter-scale fix).
- Help text hides while the lap map is out.
- Full bot match with the new autopilot parking: the Mark bot completes its whole route
  (trip harness: 8/8, then 15/16 trips parked).

## Gameplay ideas
- "Eyeball" handoff: show which Tail has the Mark in sight; reward handoffs, penalise sitting on him.
- Quick team pings: "seen here, heading N" arrows on teammates' maps.
- Suspicion meter for the Mark (same car behind across turns/grid squares).
- Driver animation: mouth flap from voice chat, blinks, head turns on mirror glances, hands on
  the wheel, arm out of the window, wave emote.
- Online play beyond LAN: Unity Relay or Steam networking (needs accounts).
- Practice round / tutorial; bots filling empty Tail slots in the lobby.
- Player customisation in the lobby (from the shared cosmetic pool, so players still blend in).

## Art (friendslop pass)
- Distance LODs for vehicles (~6k verts each; fine at 2–4 ms here, heavy for low-end PCs) —
  export a low-bevel LOD from the Blender script and use a LODGroup or distance swap.
- Thinner A-pillars on the `_Player` variants (the cockpit pillar is chunky).
- Done: business buildings, house kit + yards, lot dressing, trees, live cockpit (wheel/hands/gauges).
  Next: downtown office variety (setbacks, ground-floor shops, rooftop kit), industrial kit,
  street clutter on sidewalks (hydrants, bins, benches), night check of lot lamps/porch lights.
- House geometry is ~6M verts across the town (unique per instance). If memory matters: GPU
  instancing per variant, or a low-detail LOD for distant tiles.
- Check the cockpit rig on the van, bus, box truck and pickup (eye/dash geometry differs).
- Driver animation (see gameplay ideas): mouth flap from voice, blinks, head turns.
- Car accessories from the shared pool (roof boxes, antennas, stickers) as extra callout clues.
- Check the target-card portraits and the replay/debrief with the new art.
