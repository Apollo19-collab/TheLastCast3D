// Exports The Last Cast's game data and world from the web version's shared
// modules into files the Unity game loads (Assets/Resources):
//
//   GameData.json  fish, rarities, tackle, water zones, areas, shops, docks, hotspot settings
//   World.bytes    the world sampled every GRID world units:
//                    int16 shoreline distance (world units; + land, - water)
//                    uint8 ground type (0 grass, 1 sand, 2 rock, 3 dirt; 255 water)
//                    uint8 water zone index into GameData.zones (255 = land)
//                    uint8 forest density (0..255)
//
// Run from this folder: node export-data.mjs [path/to/TheLastCast] [outDir]

import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const src = path.resolve(process.argv[2] ?? '../TheLastCast');
const out = path.resolve(process.argv[3] ?? '../unity-src/Assets/LastCast/Resources');
const load = (p) => import(pathToFileURL(path.join(src, p)).href);

const { SPECIES, RARITY, FAMILIES } = await load('shared/fish.js');
const { ITEMS, STARTER } = await load('shared/gear.js');
const { LOCATIONS, fieldAt, segmentDistance, shapesDistance, warpPoint, zoneAt } = await load('shared/world.js');
const { fbm, noise } = await load('shared/noise.js');
const { ZOO, PETS, PET_RARITIES } = await load('shared/pets.js');
const { SETS, ARMOUR_SLOTS, ARMOUR_SLOT_LABELS } = await load('shared/armour.js');
const { ACHIEVEMENTS } = await load('shared/achievements.js');
const { CHUM, CHUM_TABLE } = await load('shared/chum.js');
const { EVENT_TYPES, EVENT_TUNING, EVENT_SLOT, EVENT_LEAD, eventPlaces } = await load('shared/events.js');
const {
  BOAT, VOYAGE, SEA_EVENTS, SEA_LOCATIONS, BOSS, BOSSES, BOSS_ATTACKS, DECK_AREAS, MISSIONS, SEA_BOAT, TIMES_OF_DAY,
  makeSeaWorld, seaZone, bossZone,
} = await load('shared/voyage.js');
const { XP } = await load('shared/levels.js');
const { RIVER } = await load('shared/world.js');

const world = LOCATIONS.mirrorLake;
const GRID = 10; // world units per sample (1 m in Unity)

// ---- ground: the same rules as the web client's terrain (client/js/gfx/terrain.js) ----

const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
const smoothstep = (a, b, v) => {
  const t = clamp((v - a) / (b - a), 0, 1);
  return t * t * (3 - 2 * t);
};
const trails = (world.shape.trails ?? []).map((pts) => {
  const xs = pts.map((p) => p[0]);
  const ys = pts.map((p) => p[1]);
  return { pts, x0: Math.min(...xs) - 40, x1: Math.max(...xs) + 40, y0: Math.min(...ys) - 40, y1: Math.max(...ys) + 40 };
});
function trailDistance(x, y) {
  let d = Infinity;
  for (const t of trails) {
    if (x < t.x0 || x > t.x1 || y < t.y0 || y > t.y1) continue;
    for (let i = 1; i < t.pts.length; i++) d = Math.min(d, segmentDistance(x, y, t.pts[i - 1], t.pts[i]));
  }
  return d;
}
function groundType(x, y, s) {
  const sh = world.shape;
  const [wx, wy] = warpPoint(sh, x, y);
  const rough = (fbm(x / 45 + 3, y / 45 + 7, 2) - 0.5) * 50;
  if (shapesDistance(sh.rock, wx, wy) + rough < 0 && (s < 140 || fbm(x / 110 + 5, y / 110 + 9, 2) > 0.5)) return 2;
  if (s > 90 && fbm(x / 300 + 17, y / 300 + 3, 3) > 0.76) return 2;
  if (s < 420 && shapesDistance(sh.beaches, wx, wy) + rough < 0) return 1;
  const beach = 2 + 50 * smoothstep(0.55, 0.8, fbm(x / 420 + 9, y / 420 + 2, 2));
  if (s < beach + rough * 0.3) return 1;
  if (trailDistance(x, y) < 14 + (noise(x / 20, y / 20) - 0.5) * 10) return 3;
  return 0;
}
const forestAt = (x, y) => smoothstep(0.47, 0.66, fbm(x / 650 + 40, y / 650 + 20, 3));

// ---- GameData.json ------------------------------------------------------------------

const zones = world.zones.map((z) => ({
  id: z.id, name: z.name, kind: z.kind ?? z.id, biteRate: z.biteRate ?? 1, label: z.label !== false, decor: z.decor ?? null,
  rect: z.rect ?? z.rects?.[0] ?? null, fish: z.fish,
}));
const zoneIndex = new Map(world.zones.map((z, i) => [z, i]));
const data = {
  source: 'The Last Cast web version (shared/*.js)',
  width: world.width,
  height: world.height,
  grid: GRID,
  spawn: world.spawn,
  rarities: RARITY,
  families: FAMILIES,
  species: Object.entries(SPECIES).map(([id, s]) => ({ id, ...s })),
  starter: STARTER,
  items: Object.entries(ITEMS).map(([id, it]) => ({ id, ...it, affinity: it.affinity ?? {} })),
  zones,
  areas: world.areas.map((a) => ({ id: a.id, name: a.name, rect: a.rect, label: a.label ?? null })),
  shops: world.shops.map((s) => ({ kind: 'bait', awning: ['#e63946', '#f1faee'], sign: 'BAIT', keeper: '', greeting: '', minimap: '#ffd166', ...s })),
  structures: world.structures.map((r) => ({ x: r.x, y: r.y, w: r.w, h: r.h, type: r.type })),
  solid: (world.solid ?? []).map((r) => ({ x: r.x, y: r.y, w: r.w, h: r.h })),
  hotspots: world.hotspots,
  zoo: { interval: ZOO.interval, stockSize: ZOO.stockSize, range: ZOO.range, spots: ZOO.spots },
  petRarities: PET_RARITIES,
  pets: Object.entries(PETS).map(([id, p]) => ({ id, ...p })),
  armourSlots: ARMOUR_SLOTS,
  armourSlotLabels: ARMOUR_SLOT_LABELS,
  sets: Object.entries(SETS).map(([id, s]) => ({ id, ...s })),
  achievements: ACHIEVEMENTS,
  chum: { ...CHUM, table: CHUM_TABLE },
  events: { slot: EVENT_SLOT, lead: EVENT_LEAD, types: EVENT_TYPES, tuning: EVENT_TUNING, places: eventPlaces(world) },
  boat: {
    x: BOAT.dock.x, y: BOAT.dock.y, heading: BOAT.dock.h, length: BOAT.length, beam: BOAT.beam,
    interval: BOAT.interval, arrive: BOAT.arrive, board: BOAT.board, depart: BOAT.depart,
    boardRange: BOAT.boardRange, landing: BOAT.landing,
    // In 3D the boat comes into view a few hundred metres up the East River, rather than
    // racing the whole river (as fast as the 2D boat would have to) from off the map.
    path: [...[...RIVER].reverse().filter(([x]) => x > 2900 && x <= 4100), ...BOAT.path.filter(([x]) => x <= 2900)],
  },
  voyage: (() => {
    const sea = makeSeaWorld();
    return {
      timing: VOYAGE,
      times: TIMES_OF_DAY,
      xp: XP.voyage,
      seaBoat: SEA_BOAT,
      width: sea.width,
      height: sea.height,
      deck: sea.structures.map((r) => ({ x: r.x, y: r.y, w: r.w, h: r.h })),
      cabin: sea.solid[0],
      hotspots: sea.hotspots,
      crowdPenalty: sea.crowdPenalty,
      castFail: sea.castFail,
      openSea: seaZone(null),
      events: Object.entries(SEA_EVENTS).map(([id, e]) => ({ id, ...e })),
      locations: Object.entries(SEA_LOCATIONS).map(([id, l]) => ({ id, ...l, zone: seaZone(id, false), eventZone: seaZone(id, true) })),
      boss: BOSS,
      bosses: Object.entries(BOSSES).map(([id, b]) => ({ id, ...b, zone: bossZone(id) })),
      attacks: Object.entries(BOSS_ATTACKS).map(([id, a]) => ({ id, ...a })),
      deckAreas: Object.entries(DECK_AREAS).map(([id, a]) => ({ id, ...a })),
      missions: MISSIONS,
    };
  })(),
};

// ---- World.bytes ---------------------------------------------------------------------

const W = Math.ceil(world.width / GRID);
const H = Math.ceil(world.height / GRID);
const sdf = new Int16Array(W * H);
const ground = new Uint8Array(W * H);
const zone = new Uint8Array(W * H);
const forest = new Uint8Array(W * H);
const t0 = Date.now();
for (let j = 0; j < H; j++) {
  const y = (j + 0.5) * GRID;
  for (let i = 0; i < W; i++) {
    const x = (i + 0.5) * GRID;
    const k = j * W + i;
    const s = fieldAt(world, x, y);
    sdf[k] = clamp(Math.round(s), -32000, 32000);
    if (s < 0) {
      ground[k] = 255;
      const z = zoneAt(world, x, y);
      zone[k] = z ? zoneIndex.get(z) : 255;
    } else {
      ground[k] = groundType(x, y, s);
      zone[k] = 255;
    }
    forest[k] = Math.round(forestAt(x, y) * 255);
  }
  if (j % 100 === 0) console.log(`row ${j}/${H} (${((Date.now() - t0) / 1000).toFixed(1)} s)`);
}
const header = new Int32Array([0x4c43_5754, 1, W, H, GRID]); // 'LCWT', version, size, grid
const bytes = Buffer.concat([
  Buffer.from(header.buffer), Buffer.from(sdf.buffer), Buffer.from(ground.buffer), Buffer.from(zone.buffer), Buffer.from(forest.buffer),
]);

await mkdir(out, { recursive: true });
await writeFile(path.join(out, 'GameData.json'), JSON.stringify(data));
await writeFile(path.join(out, 'World.bytes'), bytes);
console.log(`Wrote ${data.species.length} species, ${data.items.length} items, ${zones.length} zones; world ${W}x${H} (${(bytes.length / 1e6).toFixed(1)} MB) in ${((Date.now() - t0) / 1000).toFixed(1)} s`);
