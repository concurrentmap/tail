# TAILED — Game Design (working title)

> A 3–6 player co-op/competitive driving game. One player runs errands across a
> living city. Everyone else is trying to follow them without getting made.

## 1. Pitch

**One Mark, many Tails.** The Mark has a secret list of stops to make across town.
The Tails, each in their own car, have to follow and map every stop — while
driving *exactly like a boring NPC*, because at every stop the Mark gets to look
back at who was behind them and burn anyone who looks suspicious by calling out
their make, model and plate.

The core skill for Tails is **"drive like an NPC."** The core skill for the Mark is
**"notice who isn't one."** The traffic simulation is the game — it is the
camouflage.

### Why it's friendslop

- Tiny ruleset, instantly readable: *follow them / lose them*.
- Emergent comedy: a Tail panicking and running a red light to keep up, the Mark
  making a third lap of the same roundabout, two Tails accidentally following
  each other.
- Real mirrors and blind spots: the Mark leans to check a wing mirror at a red
  light; a Tail realises they've been sitting in the Mark's blind spot for two
  blocks and nobody noticed.
- (Later) proximity voice leaks between cars with windows down — the Mark can
  overhear "OK, I'm three cars behind him, the blue van."
- Post-round **debrief replay** shows everyone's real paths over the map. That's
  the clip people share.
- Rotating roles every round; rounds are 12–18 minutes.

## 2. Roles

### The Mark (1 player)
- Receives a **route briefing**: 4–6 checkpoints (only the Mark sees them) and
  one final **Safehouse**.
- Chooses the order of checkpoints (except Safehouse, always last).
- At each checkpoint: park in the marked bay and **dwell** (10–20 s). During the
  dwell the **Counter-surveillance window** opens (see §4).
- May make **dry-cleaning stops**: fake dwells anywhere to bait Tails into pinning
  wrong locations.
- Drives a car assigned from the NPC fleet pool — i.e. the Mark is also
  anonymous at the start; Tails get a brief "pickup" at the start (see §5).

### The Tails (2–5 players)
- Each drives their own car, picked from the **motor pool** before the round.
- Goal: **pin** every real checkpoint on the shared team map.
- Can see each other on the team map and use a team radio; cannot see the Mark
  on any map — only with their own eyes.
- Can **tag** the Mark's car when they have line of sight, which briefly
  highlights it for teammates who also have LOS (so handoffs work).

## 3. Round flow

```
Lobby ─► Motor Pool (Tails pick cars) ─► Briefing (Mark plans order)
      ─► Pickup ─► Leg 1 ─► Checkpoint 1 ─► Leg 2 ─► ... ─► Safehouse
      ─► Marking (Tails mark the stops) ─► Reveal + Debrief
```

1. **Motor pool (60 s).** Tails spend a shared budget on vehicles. Common cars
   (grey sedans, white vans) are cheap; distinctive cars (orange hatchback,
   convertible) are cheaper still (a trap) or free. Budget forces the team to
   mix. Plates are random from the same generator as NPC plates.
2. **Briefing (45 s).** Mark sees the city map with checkpoints and orders them.
3. **Target overview.** During the briefing Tails get the Mark's full description:
   rendered portraits of the exact car and driver, model + colour, hat, and the
   plate (hold **I** to see it again while driving).
   **Pickup:** the Mark leaves a known starting location; Tails start 200–400 m away.
4. **Legs.** Free driving. Traffic, signals, weather.
5. **Checkpoint.** The Mark dwells; that's their flag window. For the first few
   seconds of a *real* stop a gold **errand marker** flashes over the Mark's car
   (and a ring pulses on the ground) — anyone looking at the car sees it, nothing
   is computed about who can. Fake (dry-cleaning) stops show none. Tails remember.
   The Mark has no sat-nav: each stop is given as a name and address ("Stop 2:
   Golden Bakery — D4, Harbor St"); the bay lights up only within ~80 m.
6. **Safehouse.** The final stop; the drive ends there (or when time runs out).
7. **Marking (≈75 s).** All Tails, together on one shared map, mark where they
   think the Mark stopped — up to one mark per stop on the route, snapping to
   businesses. Each Tail's own route is drawn to jog memories. Nothing is pinned
   during the drive.
8. **The reveal** (party-game style, Mario Party / Pummel Party): stop by stop —
   drumroll — "FOUND by Ann!" or "Missed!" (or "never reached"); then flag by flag —
   "Grey Sedova Classic, plate ABC 12??" — "It was Bob!" or "Just traffic!"; the two
   totals face off and the winner is crowned with a fanfare and confetti.
9. **Debrief (2 minutes, host can skip).** A scrubbable replay of everyone's
   paths on the gridded map:
   - **Controls:** play/pause, ±15 s, 1–30× speed, and a timeline with ticks for
     stops, pins, flags and tags.
   - **The map:** stops, pins, flags ("BURNED Bob") and tags pop up where and when
     they happened. A tether line shows any Tail within 80 m of the Mark.
   - **Live panel:** the Mark's grid square and street, and each Tail's distance
     ("12 m — right on his bumper!").
   - **Highlights:** *Closest shave* (who got nearest the Mark, when and where) and
     *Glued on* (who spent longest within 60 m), plus each Tail's time near the Mark,
     flag hits and misses, and fake stops. That's where the table laughs and learns.
   - **Result:** the reveal's outcome, stop by stop.

## 4. The Mark's counter-surveillance

The Mark's information comes only from what they actually saw *while driving*
(see §12 for the camera and mirror model).

- **Glance & focus.** Holding **1 / 2 / 3** (d-pad up for the rear-view) snaps the
  driver's eyes to the left, rear-view or right mirror; the view narrows so the
  mirror fills ~70% of the screen, and holding **right mouse** (right-stick click)
  while glancing fills it completely. Focus without a glance zooms whatever
  you're looking at. While you glance, you're not watching the road.
- **Mirrors (primary).** Rear-view and both wing mirrors are physically simulated
  reflections from the driver's eye. Checking them is a live skill: glance at the
  wrong moment and you miss the plate; lean to see around the headrest.
  Plates are only legible within limits of optical distance, angle, light and
  weather (architecture §6).
- **Over-the-shoulder / side glances** in first person — direct look, no mirror
  distortion, but you're not watching the road.
- **Glance snapshots.** A limited number (e.g. 6/round) of manual "photos" of
  whatever the Mark is looking at *right now* — including into a mirror. The
  snapshot keeps the legibility it had at that instant; it doesn't magically
  sharpen a plate.
- *(Optional later mode: a scrubbable rear dashcam roll. Left out by default
  because it replaces live mirror-checking.)*

Between checkpoints the Mark keeps a quick **notepad** (hotkey, doesn't pause the
game) for jotting plates — typing while driving is its own comedy.

**Flagging.** During the dwell the Mark files flags: `(make/model, plate)`.
- **Identified** if the model matches exactly and the plate matches ≥ 5 of 7
  characters in position (tunable). Partial plates are allowed — that's the
  tension: flag now on a partial, or wait for a better look?
- Every flag costs one of the Mark's **flag tokens** (5 per round).
- **Results are hidden until the reveal.** Nobody is burned mid-round; everyone
  tails to the end. The Mark doesn't know whether a flag hit a Tail or an NPC.

## 5. The Tails' toolkit

- **Tag** (LOS ping on the Mark's car, 15 s, teammates with LOS see it).
- **Remember** the stops (the gold errand marker, the notepad, the team radio):
  the team marks them together after the drive (§3).
- **Plate swap** (1 per Tail per round): at any fuel station, swap plates in 8 s
  while stationary. Invalidates any partial plate the Mark noted.
- **Car swap** at rental lots (costs budget + time).
- **Team radio** (hold B) — Tails and Spotters only; the Mark never hears it.
- Real tradecraft is emergent and rewarded: **leapfrogging** (hand the follow to
  a teammate at a turn), **parallel routes**, **floating box** at intersections,
  staying in the NPC flow.

## 6. Spotters (eliminated players)

Friendslop rule: nobody spectates a black screen. Players who join mid-round
become **Spotters** until the next round: they cycle through the city's **traffic
cameras** (fixed CCTV at major intersections, named like callouts — "CAM 03 · D4 ·
Harbor St, looking west") and call out sightings on team radio. They join in the
marking at the end.

## 7. Voice

- **Team radio** for Tails (PTT, radio filter).
- **Proximity voice** from every car (open mic with voice detection, N mutes).
  Windows (Y) control the leak radius: both down 16 m, one down 9 m, both up 3 m.
  The Mark hears Tails who forget to close their windows; Tails can hear the Mark
  talking to themselves.
- Horns, radio music from cars, etc. are all diegetic.

## 8. Scoring & win condition

Two numbers per round, nothing computed during the drive:

- **Locations found** — stops the Mark actually completed (checkpoints and the
  safehouse) that the Tails' marks identify (a mark within 45 m of the business;
  each mark finds at most one stop).
- **Tails identified** — distinct Tails the Mark's flags matched.

**More locations than Tails identified → the Tails win. More Tails identified than
locations → the Mark wins. Equal → draw.** Round winners get a round win; role
rotates so everyone is the Mark once; most round wins takes the match.

## 9. The traffic *is* the design

The Mark's only detection method is spotting behaviour that deviates from NPCs.
So NPC behaviour must be:

- **Plausible and varied** — driver personalities (cautious, aggressive, lost,
  distracted) so that some NPCs *also* do odd things: late lane changes, U-turns,
  slow drivers, a few who ignore amber. This creates false positives.
- **Coincident** — a **Traffic Director** occasionally assigns NPCs trips that
  overlap the Mark's route for a while (decoy followers). This stops "anyone
  behind me for 3 turns is a Tail."
- **Identity-identical** — NPC cars are drawn from the exact same model/colour
  /plate pool as player cars. Player plates are indistinguishable.

Tails stand out when they: run reds, tailgate, change lanes to mirror the Mark,
pull over when the Mark pulls over, speed through suburbs, make illegal U-turns.

### 9a. Motion tells (closed from both sides)

Early builds had a physical giveaway: NPCs rode dead-centre on lanes with
perfectly smooth speed while human cars wandered and braked unevenly. Now:

- **NPCs drive like people.** Each drifts within its lane (a slow wander keyed
  to distance travelled, ±8–40 cm by personality, frozen when stopped), holds a
  target speed that drifts a few percent over 10–30 s, feeds the throttle in
  rather than stamping on it, pauses 0.2–2.6 s before moving off when a light
  goes green or the car ahead pulls away, and stops a personal distance short of
  the line. Bodies yaw with their actual path, so wander and lane changes read
  as steering, not sliding; lane changes only move sideways while rolling.
- **Players can drive like NPCs.** Optional **lane-keep** (L) nudges the wheel
  to the lane centre when you aren't steering, follows plain bends and hands
  back before junctions; any steering or an indicator overrides it. **Adaptive
  cruise** (K) holds a set speed, keeps a 1.6 s gap, eases off for junctions and
  stops for red lights and stop signs (W to go; brake cancels). Blending in is a
  skill you can learn, not something the physics forbids.
- Stopped player cars show brake lights, like every NPC waiting at a light.

### 9b. NPC variety

- **Buses** (NPC-only, yellow livery) run the town and stop at every bus stop
  for 7–14 s, signalling in and out. **Box trucks** and **delivery vans** pull
  over and **double-park with hazards** for 20–45 s at loading boxes on wide
  roads. Traffic goes round them (lane change) or waits behind: a Tail stuck
  behind a van is a natural reason to be slow, and a Tail with hazards on is
  just another van.
- **U-turns** at non-signalled junctions: lost drivers realise they're heading
  the wrong way, and some drivers whose new trip lies behind them turn round
  rather than go round the block. U-turning is therefore *not* an automatic
  Tail tell.
- Buses and lorries are never in the motor pool, the flag form or the Mark's
  random car.

### 9c. A lived-in town (no pedestrians)

- Household cars on most suburban driveways and vans or lorries at warehouse
  doors: solid, plated, motionless. That's ~1,200 extra cars of cover to sit
  among (their plates come from a separate series, so they never match a
  moving car).
- Bus shelters (bench, advert, BUS flag, painted bay) and yellow loading boxes
  mark where kerb stops happen.
- Trees sway in the wind (more in rain); low-poly clouds drift over town and
  grey over when it rains.
- Ambient sound: distant traffic rumble, birds by day, crickets at night, rain
  hiss, and engine notes on the six moving NPC cars nearest you.

## 10. World

- **A fictional town, procedurally generated from a seed** (~1.6 × 1.6 km for the
  MVP). Every match can be a fresh town — nobody can memorise routes — or a
  shared "town of the day" seed, PEAK-style.
- Districts with distinct driving character:
  - **Downtown** — dense grid, towers, signals every block, short sightlines.
  - **Suburbs** — irregular streets, merged blocks, all-way stops, gabled houses;
    (later) cul-de-sacs that trap careless Tails.
  - **Industrial** — huge blocks, warehouses, long empty roads where a Tail has
    nowhere to hide.
  - **Parks** — open ground that breaks up the grid.
  - A straight **arterial grid** (2 lanes each way, signals) ties it together,
    wrapped by a boundary road and a treeline marking the edge of the map.
- Later: gentle curves and hills to shorten sightlines (straight 1.6 km arterials
  favour the Mark too much), ring highway with ramps, multi-storey car park,
  tunnel, drawbridge on a timer.
- Checkpoints drawn from generated POIs: petrol stations, drive-throughs,
  warehouses, car washes, laundromats, a pier.
- Time of day and weather randomised per round: night hurts plate legibility
  for the Mark but headlights hide car models for Tails; rain shrinks both.

## 11. Vehicles

- ~24 fictional make/models (parody brands, no real trademarks) × colour
  palette. Rarity weights mirror a real fleet (lots of grey/white/black
  sedans/SUVs, few bright sports cars).
- **Every model must be identifiable by silhouette alone** at mirror distance —
  the Mark flags make/model, so shape language is gameplay: boxy van, bubble
  hatch, long-bonnet sedan, tall SUV, pickup with a bed, tiny kei car.
- Handling: arcade-leaning but weighty. Friendslop slapstick allowed (spin-outs,
  fender benders cause NPCs to stop and honk — which draws attention!).
- Damage is cosmetic but visible — a dented bumper is an identifying mark.

## 12. Camera, cockpit & mirrors

Every player can switch at any time between:

- **First person (cockpit).** Head-look (mouse / right stick) with a comfortable
  yaw range and a **lean** input (shift the head ~15 cm left/right/forward).
  Interior is modelled per vehicle class: A-pillars, headrests, rear pillars and
  cargo walls produce real **blind spots** — a van's rear-view mirror shows the
  inside of the cargo doors, so van drivers live on wing mirrors.
- **Third person (chase).** Forward-biased chase camera for comfortable driving
  and spatial awareness. It deliberately gives **no plate information**: plate
  legibility is always computed from the *driver's eye*, directly or via a
  mirror, never from the chase camera (architecture §9). In third person a small
  **mirror strip** HUD shows the car's real rear-view + wing mirror textures, so
  the Mark can still play from the chase cam at a readability cost. Camera height
  is capped so Tails can't peek over traffic or round corners.

**Mirrors are simulated optically:**
- Flat interior rear-view mirror and driver-side wing mirror; **convex**
  passenger-side mirror (wider view, everything smaller — "objects are closer
  than they appear" — harder to read plates and judge distance).
- What each mirror shows depends on the head position — leaning changes the
  view, exactly like a real car.
- Dirt, rain droplets and vibration on wing mirrors; rear-view mirror night
  **dip** mode (dims headlight glare but darkens the image).
- **Oversized plates.** Plates are 1.7× real size (chunky, on a dark bracket, lamps
  outboard) — the one exaggerated detail in the art style, because reading them is
  the game. **Reading range (1920×1080, no zoom, no HUD help):** straight ahead crisp
  at 15 m, readable to ~25–30 m, gone by ~45 m; in the rear-view mirror a follower's
  plate reads at 30 m+. The convex passenger mirror roughly halves that. Tails must
  hang back further than before (or use rain and night) — the Mark's own plate is just
  as readable to them.
  Queued cars in the next lane mostly hide each other's plates in a wing mirror.
- **Plate readouts:** when you look straight at a plate (near the centre of your view,
  clear line of sight) or glance at a mirror that shows one, a big copy of the plate
  pops up beside it, blurred exactly as much as your eye's legibility allows. No zoom
  needed: straight ahead a plate reads to ~20 m, in the rear-view mirror a follower's
  plate to ~16 m behind; beyond that the readout stays a smudge. Now an accessibility
  menu toggle, **off by default** (the big plates do the job on their own).
- Mirrors physically reverse text. By default plates seen *in a mirror* are
  drawn pre-reversed so they read normally ("readable" setting); a "realistic"
  menu setting shows true mirror-writing.
- Tails can exploit this: sit in the blind spot, stay directly behind a
  high-roofed van, or hang far enough back that the plate never resolves.

## 12a. Navigation & callouts

- The Mark gets **no turn-by-turn**: each stop's name and address on the HUD
  ("Stop 2: Golden Bakery — D4, Harbor St"), every stop marked on the map
  (numbered; red to visit, the next one bigger, green once visited), and a tall
  light column over every stop in the world (red, the next one bright and pulsing,
  green once visited) — all local to the Mark. The exact bay lights up within ~80 m.
- Every street is named, signed at junctions and labelled on the map.
- **Map grid:** every map carries a road-atlas grid (`MapGrid`): letters A–H west →
  east, numbers 1–8 north → south, ~200 m squares. North is always up. Calls sound
  like "he's in D5, heading west on Heather Ln".
- **The map is stowable.** **M** pulls the paper map out onto your lap (passenger
  side). You can drive with it out, but it blocks that side of the view and your
  attention; it shows your grid square, street and heading ("D5 · Heather Ln ·
  heading west") plus role markers (route, team). **M** again stows it. Mouse wheel
  or **+/−** zooms it (up to 6×), centred on your car.
  **Shift+M** spreads it out full-screen (blocking): wheel zooms around the cursor,
  drag pans. The marking map and debrief map zoom the same way.
  With the map stowed you have only the **dashboard compass** ("W 259°") and the
  street signs, so knowing where you are is a skill.
- Landmarks (clock tower, water tower) give everyone shared reference points.
- Marks (at the end) snap to a business when dropped near one.

## 13. Art direction

Friendslop, not photorealism. The genre's real principle is **readability over
detail** (clear silhouettes, big shapes, clean colour separation, personality
from motion) rather than "low poly" for its own sake.

- **World:** a toy-town diorama. Flat vertex colours, faceted low-poly shapes, no
  textures except where information lives (plates, signs). Pastel buildings,
  saturated lawns, dark asphalt so cars pop against the road. Bright gradient
  sky, soft sun, cool-tinted shadows, light haze for depth.
- **Smooth, not pixelly:** rounded body silhouettes, smooth-shaded characters and
  tyres, anti-aliased edges, and a clean sans for plates and signs.
- **Vehicles:** chunky, slightly toy-like proportions (big wheels, rounded
  cabins), bouncy suspension, exaggerated body roll and squash on bumps. Plates
  are the one high-detail element — big, chunky, legible glyphs.
- **Drivers:** simple bean-ish characters visible through the windows,
  bobbling with the car. **Cosmetics never break anonymity:** player hats and
  outfits come from the same pool NPC drivers wear, with the same rarity — a
  rare hat stands out exactly like a rare car.
- **Comedy through physics and motion:** fender-benders make NPCs stop, honk and
  gesture; cars wobble and bounce; horns are expressive.
- **Readable signals:** traffic lights, stop signs and markings are exaggerated
  in size and colour, because the core skill is "drive like you obey them".

## 14. Scope for a first playable (MVP)

- 1 map, 4 players (1 Mark + 3 Tails), 8 vehicle models, day only.
- Traffic: lanes, signals, IDM car-following, lane changes, routing, ~400 NPCs.
- First/third-person camera with simulated mirrors and blind spots.
- Checkpoints, dwell, flagging (mirrors, glances, snapshots, notepad), pinning,
  scoring.
- Debrief replay.

Built beyond the MVP: voice, spotters, dusk/night/rain, plate & car swaps, chop
shops, a new town per seed. Still deferred: dashcam roll mode, drawbridge,
cul-de-sacs, hills, cosmetics shop, Steam/Relay online play.
