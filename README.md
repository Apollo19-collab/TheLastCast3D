# The Last Cast: first person

A first-person version of The Last Cast, made in Unity 6 (URP) with a stylized low-poly
look. Play on your own, or host a game for up to 8 friends.

## Play

Run `Game\TheLastCast.exe`. Your progress saves to
`%USERPROFILE%\AppData\LocalLow\The Last Cast\The Last Cast\profile.json`.

| | |
| --- | --- |
| Move / sprint / hop | WASD, hold Shift, Space (a small hop). With a line out you can still walk, slowly |
| Cast | Hold the left mouse button, release. A ghost bobber shows where it will land (green on water, red where it can't) |
| Hook / reel | Click when the bobber dips, then hold to reel; let go when the fish pulls |
| Reel in | Right-click or R (or walk too far from your bobber) |
| Shops, the zoo, the boat | E at a shop counter, the Travelling Zoo wagon, or the voyage boat when it's docked |
| At sea | E beats off whatever has grabbed the rail in the boss fight |
| Chum bucket | C to put one down (buy them at the Bait Shop); C beside it picks it up again, with the time and fish it had left |
| Menus | G tackle, R armour, P pets, I fish index, T goals, K trophies, H history, V events, M map, N online, O / Esc options (General, Graphics, Controls) |
| Fast travel | M for the map, click a place (or any dry land), then Travel. Not from the boat or with a line out |
| Chat | Enter (when you're in a multiplayer game) |

### What's in it (from the web game)

- 110 fish, 86 rods, reels, lines and baits, the Bait Shop and the three travelling tackle shops
- Levels 1-75, trophies and named giants, hotspots, and the Angler's Map (free at the Bait Shop): M
  shows the whole world, and clicking a place or any dry land on it fast-travels you there
- **Pets**: all 30, with their abilities. The Travelling Zoo moves around the lake every 15 minutes
  with three animals (the same ones as the web game at that moment); your pet follows you around.
  Each is modelled on the real animal (or the creature it's named for) and moves like it: legs walking
  in step, wings flapping, tails wagging, tentacles rippling, a crab's claws clacking.
- **Armour**: all 15 sets, with per-piece perks and full-set effects. Every piece has its own model (an
  oilskin coat, a pea coat, a brass diving helmet, ember plate...), mix and match; the Armour tab shows
  your angler in 3D (point at a piece to try it on), and other players see exactly what you wear
- **Achievements**: every goal that doesn't need a missing feature, with coin rewards; tackle
  they unlock needs them again
- **Chum buckets** and **map events** (Feeding Shoal, Great Haul, Golden Tide), on the web game's
  clock, scaled for one angler
- **Boat voyages**: every 15 minutes (on the quarter hour, the same clock as the web game) a boat
  comes up the East River (the drawbridge lifts to let it through) and docks at the South Beach
  dock for two minutes. Press E to board.
  Out at sea you fish from a trawler's deck at 3 of the 8 sea locations, each with its own fish,
  legendary, time of day and special event (Feeding Frenzy, Treasure Tide, Squall...), with three
  crew missions.
- **The boss** at the end of every voyage: the Kraken, Megalodon, the Sea Serpent or the Ghost
  Whale, with four minutes to beat it. Land fish to hurt it (double in its red weak spot), cast
  into the golden ring when it breaches to harpoon it, mash E on whatever grabs the rail, and get
  out of the red part of the deck before it slams. Everyone is paid at the end: points, missions,
  rank, the boss, and a share of the prize pool.
- **Each boss fights like what it is**, and you see every attack happen; the boat rolls and pitches
  when it's hit:
  - the Kraken: a tentacle rears up and slams across the deck, tentacles burst up all round and
    thrash, an ink cloud spreads round the boat
  - Megalodon: it charges in, fin out, and rams the hull; bites the rail by the stern and rolls;
    circles the boat in a frenzy; its head clamps onto the rail
  - the Serpent: coils swing up over the deck and crush it; it swims circles round the boat (a
    maelstrom); it churns up silt
  - the Ghost Whale: its tail rises and slams onto the deck; it leaps clear of the water and the
    wave rocks the boat; a ghostly mist rolls in; its song spreads in rings over the water

  To watch one boss's attacks without playing: `Game\TheLastCast.exe -screen-fullscreen 0 -autoshot
  shots -attacks kraken` (or megalodon, serpent, ghostwhale).

### Multiplayer (up to 8 anglers)

- **Host:** on the title screen (or the Online tab, N) choose *Host a game*. You get a short
  join code; send it to your friends. Tick *Private* to keep the game out of the public list.
- **Join:** *Join a game*, then type the code (or pick a game from the list of open games).
- Works over the internet with no router setup: connections go through Unity's online
  service (free tier), linked to this project's Unity Cloud project
  (`unity-src\cloud-project.txt`). Everyone must play the same build of the game, so share
  the whole `Game` folder (zip it) with your friends.
- Shared: everyone's angler (walking, running, hopping, casting and reeling as they do), line
  and pet, where they are (a dot in their colour on the minimap and the Angler's Map, with
  their name; anyone out at sea is listed under the map), catches, trophies and level-ups, chat (Enter),
  the hotspots (everyone can fish the same one, with no penalty for company), chum buckets and the map
  events, which become co-op again (goals grow with the group, everyone who helps is paid).
- **Voyages together:** everyone who boards the same boat sails the same voyage (same stops,
  events, missions and boss), sees each other on deck, and shares the missions, the boss's
  health (it scales with the crew) and the points table.
- Each player keeps their own progress, coins, tackle and pets on their own PC.
- If the host leaves, everyone carries on fishing on their own.

To test with two copies on one PC:
`Game\TheLastCast.exe -screen-fullscreen 0 -nettest host test` and, in a second window,
`Game\TheLastCast.exe -screen-fullscreen 0 -nettest join test` (screenshots go to `test\`).
Add `-netvoyage` to both and they then sail a short voyage together.

Not yet: duels and the Hall of Records.

## Updates

The game updates itself from this repo's releases
(https://github.com/Apollo19-collab/TheLastCast3D/releases). When a newer version is out, the
title screen and the Options tab (O) offer **Update now**: the game downloads it, closes, puts
the new files in place and starts again. Your save isn't touched.

To publish a new version (needs the GitHub CLI, signed in):

```
.\Make-Release.ps1 1.1.0 "What's new"
```

## Build

Double-click `Make TheLastCast exe.bat`. It installs and signs in to Unity if needed,
creates the project in `TheLastCastFP\`, copies the game in from `unity-src\` and builds
`Game\TheLastCast.exe`. Run it again after changing anything in `unity-src\`.

To check how things look without playing, run
`Game\TheLastCast.exe -screen-fullscreen 0 -autoshot shots`: it saves screenshots of a few
spots and menus, then a shortened voyage and boss fight, then the animations frozen at a few
moments (each boss, and other players walking, running, casting and reeling), into `shots\`
and quits, without touching your save.

## Models (Blender)

The 3D models (trees, rocks, boats, the shops, the zoo wagon, the angler and every armour piece, the rod, all
108 fish, the 30 pets and the four bosses) are made in Blender by `blender\lastcast_models.py` and
exported as FBX into `unity-src\Assets\LastCast\Resources\Models`. To rebuild them after
changing the script:

```
G:\Blender\blender.exe --background --factory-startup --python blender\lastcast_models.py
```

Add `-- tree pine` to build only some kinds, and `--preview <folder>` (with `--each` for one
picture per model) to render previews. Parts the game colours itself (a shop's awning, an
angler's jacket and hat) are painted white with a region code in the alpha channel.
Anything without a model falls back to the game's own code-built shapes.

Each fish is its own model, built on the real species from field-guide references: its body
plan (profile, fins, tail, mouth, barbels) and its own colours and markings (a pumpkinseed's red
ear spot, a brook trout's haloed spots and worm-tracks, a logperch's pig snout and bars). The
legends are built on the fish they're based on.

Moving parts are tagged in the models (the UV channel holds the limb and how far along it each
point is). The game turns them into bones (`World\LimbRig.cs`): anglers walk and cast
(`Net\RemoteAngler.cs`), the bosses' tentacles, tails, jaws, coils and flippers move
(`World\BossMotion.cs`), and a fish you land flaps its tail.

## What's where

- `unity-src\Assets\LastCast\`: the game's code, shaders and data (the source of truth;
  the build script copies it into the Unity project)
- `unity-export\export-data.mjs`: exports the world and game data from the web version
  (`TheLastCast\`) into `GameData.json` and `World.bytes`. Re-run it after changing the web
  game's fish, tackle or map: `node unity-export\export-data.mjs TheLastCast unity-src\Assets\LastCast\Resources`
- `TheLastCast\`: the web version (Node server and browser client)
