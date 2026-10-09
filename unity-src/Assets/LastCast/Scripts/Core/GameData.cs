// Game data exported from the web version of The Last Cast (see Tools~/export-data.mjs):
// fish, rarities, tackle, water zones, areas, shops and docks. Loaded once from
// Resources/GameData.json.
//
// World coordinates ("units") are the web game's: x east, y south, 10 units to
// a metre. WorldMap converts them to Unity positions.

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace LastCast
{
    public class RectU
    {
        public float x, y, w, h;
        public string type; // docks: "dock" or "bridge"
        public bool Contains(float px, float py) => px >= x && py >= y && px < x + w && py < y + h;
    }

    public class PointU { public float x, y; }

    public class RarityInfo
    {
        public string label;
        public string color;
        [JsonIgnore] public Color Color => ColorUtility.TryParseHtmlString(color, out var c) ? c : Color.white;
    }

    public class Species
    {
        public string id, name, rarity, family;
        public float minKg, maxKg, points, fight;
    }

    public class Item
    {
        public string id, slot, name, desc, shop, unlock;
        public int price;
        public int? level, pack;
        public float? range, power, bite, rare, speed, drag, strength;
        public Dictionary<string, float> affinity = new Dictionary<string, float>();
        public bool IsBait => slot == "bait";
    }

    public class Zone
    {
        public string id, name, kind;
        public string decor; // "reeds", "lilies", "current" or null
        public float biteRate = 1;
        public bool label = true;
        public RectU rect;
        public Dictionary<string, float> fish = new Dictionary<string, float>();
    }

    public class Area
    {
        public string id, name;
        public RectU rect;
        public PointU label;
    }

    public class Shop
    {
        public string id, name, kind, keeper, greeting, sign, minimap;
        public string[] awning;
        public float x, y, range;
        public bool IsTackle => kind == "tackle";
    }

    public class HotspotConfig
    {
        public int count;
        public float radius, minLife, maxLife;
    }

    public class ZooSpot { public float x, y; public string area; }
    public class ZooConfig { public float interval, range; public int stockSize; public List<ZooSpot> spots; }

    // ---- pets (shared/pets.js) -----------------------------------------------------------------
    public class PetRarity { public string label, color; public int price; public float weight; }
    public class PetZone { public string[] zones; public float bite; }
    public class PetFind { public float chance; public int min, max; }
    public class PetFx
    {
        public PetZone zone;
        public PetFind find;
        public Dictionary<string, float> affinity;
        public float? bite, rare, legendary, mythic, xp, coins, reel, tension, fight, sea, hotspot, hold;
        public float? @double, snapSave, baitSave, rescue, biteWindow, castRange;
    }
    public class Pet
    {
        public string id, rarity, name, emoji, ability, desc;
        public PetFx fx = new PetFx();
    }

    // ---- armour (shared/armour.js) -------------------------------------------------------------
    public class SetEffect
    {
        public string name, desc;
        public string[] zones;
        public float? coins, xp, bite, rare, tension, reel, weight, @double, snapSave, sea, legendary, mythic, zoneBite;
    }
    public class ArmourSet
    {
        public string id, name;
        public int level, price;
        public Dictionary<string, float> perk;
        public SetEffect effect;
        public Dictionary<string, string> pieces;
        public Dictionary<string, string> look;
    }

    // ---- achievements, chum, map events ----------------------------------------------------------
    public class Achievement
    {
        public string id, name, desc, unit;
        public Newtonsoft.Json.Linq.JToken metric; // "catches", "family.trout", { sum: [...] } or { min: [...] }
        public float goal;
        public int coins;
    }
    public class ChumRow { public float chance; public int[] uses; public Dictionary<string, float> bait; }
    public class ChumConfig
    {
        public int price, maxFish, maxOwned;
        public float duration, radius, attractRadius, biteBonus;
        public Dictionary<string, ChumRow> table;
    }
    public class MapEventType { public string name, icon, color, desc; public float duration, radius; }
    public class EventPlace { public string zoneId, name; public float x, y; }
    public class ShoalTuning { public float bite, perAngler, maxBite, rare, coinsPerFish, maxGroup, maxFish; }
    public class HaulTuning { public float goalBase, goalPerAngler, maxAnglers, bite, coins, coinsPerFish, maxFish, xp, failCoinsPerFish; }
    public class TideTuning { public float meterBase, meterPerAngler, maxAnglers, rush, points, rare, coins, coinsPerRush; }
    public class EventTuning { public ShoalTuning shoal; public HaulTuning haul; public TideTuning tide; }
    public class EventsConfig
    {
        public float slot, lead;
        public Dictionary<string, MapEventType> types;
        public EventTuning tuning;
        public List<EventPlace> places;
    }
    public class BoatMooring
    {
        public float x, y, heading, length, beam;
        public float interval, arrive, board, depart, boardRange; // seconds; world units
        public PointU landing; // where passengers step off, on the dock
        public float[][] path; // the route in, up the East River to the dock
    }

    // ---- boat voyages and the boss (shared/voyage.js) ----------------------------------------------
    public class VoyageTiming
    {
        public int stops, missions, missionReward;
        public float outbound, fishing, sailing, bossIntro, boss, results, eventEarliest, eventLength, pointsBonus;
        public int[] rankBonus;
    }
    /// <summary>Changes to everyone's fishing while a special event (or a boss attack) lasts.</summary>
    public class SeaMods
    {
        public float? biteSpeed, rareBoost, reelSpeed, tension, fight, points;
        public Dictionary<string, float> extraFish;
    }
    public class SeaEvent { public string id, name, color, desc; public SeaMods mods; }
    public class SeaLocation
    {
        public string id, name, desc, legendary, mythic, @event;
        public string[] water; // [deep, shallow]
        public float biteRate;
        public bool eventOnly;
        public Zone zone, eventZone;
    }
    public class SeaBoat { public float x, y, length, beam; }
    public class SeaHotspots : HotspotConfig { public RectU area; }
    public class BossPhase { public float at, weakSpotMove; public string name; public float[] attackEvery; }
    public class BreachTuning { public float first, window, radius, damage; public float[] every; }
    public class GrabTuning { public float first, time, hits, hitsPerAngler, range, cooldown, heal, lurch, damage; public float[] every; }
    public class BossReward { public int coins, xp, mvpCoins; }
    public class PoolTuning { public float @base, perAngler, lose, even; }
    public class BossTuning
    {
        public float @base, perAngler, weakSpotRadius, warning, grabHitValue;
        public List<BossPhase> phases;
        public BreachTuning breach;
        public GrabTuning grab;
        public Dictionary<string, float> slam; // { stun }
        public BossReward win, lose;
        public PoolTuning pool;
    }
    public class BossInfo { public string id, name, desc, color, slam, grab; public float hp; public string[] attacks; public Zone zone; }
    public class BossAttack { public string id, name, desc; public float? tension, seconds; public bool area; public SeaMods mods; }
    public class DeckArea : RectU { public string id, name; }
    public class MissionDef { public string id, text; public float perAngler, min; }
    public class VoyageData
    {
        public VoyageTiming timing;
        public string[] times;
        public int xp;
        public SeaBoat seaBoat;
        public float width, height, crowdPenalty;
        public List<RectU> deck;
        public RectU cabin;
        public SeaHotspots hotspots;
        public string castFail;
        public Zone openSea;
        public List<SeaEvent> events;
        public List<SeaLocation> locations;
        public BossTuning boss;
        public List<BossInfo> bosses;
        public List<BossAttack> attacks;
        public List<DeckArea> deckAreas;
        public List<MissionDef> missions;

        public SeaEvent Event(string id) => events.FirstOrDefault(e => e.id == id);
        public SeaLocation Location(string id) => locations.FirstOrDefault(l => l.id == id);
        public BossInfo Boss(string id) => bosses.FirstOrDefault(b => b.id == id);
        public BossAttack Attack(string id) => attacks.FirstOrDefault(a => a.id == id);
        public DeckArea DeckArea(string id) => deckAreas.FirstOrDefault(a => a.id == id);

        /// <summary>Every species you can catch at sea (the locations, their legendaries and mythics, and the bosses' waters).</summary>
        public IEnumerable<string> SeaSpecies() =>
            locations.SelectMany(l => l.eventZone.fish.Keys).Concat(bosses.SelectMany(b => b.zone.fish.Keys))
                .Concat(events.Where(e => e.mods.extraFish != null).SelectMany(e => e.mods.extraFish.Keys)).Distinct();
    }

    public class GameData
    {
        public float width, height, grid;
        public PointU spawn;
        public Dictionary<string, RarityInfo> rarities;
        public Dictionary<string, string> families;
        public List<Species> species;
        public Dictionary<string, string> starter;
        public List<Item> items;
        public List<Zone> zones;
        public List<Area> areas;
        public List<Shop> shops;
        public List<RectU> structures;
        public List<RectU> solid;
        public HotspotConfig hotspots;
        public ZooConfig zoo;
        public BoatMooring boat;
        public VoyageData voyage;
        public Dictionary<string, PetRarity> petRarities;
        public List<Pet> pets;
        public string[] armourSlots;
        public Dictionary<string, string> armourSlotLabels;
        public List<ArmourSet> sets;
        public List<Achievement> achievements;
        public ChumConfig chum;
        public EventsConfig events;

        public Pet PetById(string id) => id == null ? null : pets.FirstOrDefault(p => p.id == id);
        public ArmourSet SetById(string id) => sets.FirstOrDefault(s => s.id == id);
        /// <summary>An armour piece id is "set_slot", e.g. "clover_head".</summary>
        public (ArmourSet set, string slot) Piece(string pieceId)
        {
            if (pieceId == null) return (null, null);
            int i = pieceId.LastIndexOf('_');
            return i < 0 ? (null, null) : (SetById(pieceId.Substring(0, i)), pieceId.Substring(i + 1));
        }

        [JsonIgnore] public Dictionary<string, Species> SpeciesById;
        [JsonIgnore] public Dictionary<string, Item> ItemsById;

        static GameData instance;
        public static GameData I => instance ??= Load();

        static GameData Load()
        {
            var text = Resources.Load<TextAsset>("GameData");
            var data = JsonConvert.DeserializeObject<GameData>(text.text);
            data.SpeciesById = data.species.ToDictionary(s => s.id);
            data.ItemsById = data.items.ToDictionary(i => i.id);
            return data;
        }

        public Species Fish(string id) => SpeciesById.TryGetValue(id, out var s) ? s : null;
        public Item Item(string id) => ItemsById.TryGetValue(id, out var i) ? i : null;
        public RarityInfo Rarity(string id) => rarities.TryGetValue(id, out var r) ? r : rarities["common"];
        public Shop ShopById(string id) => shops.FirstOrDefault(s => s.id == id);

        public Area AreaAt(float x, float y)
        {
            foreach (var a in areas) if (a.rect != null && a.rect.Contains(x, y)) return a;
            return null;
        }
    }
}
