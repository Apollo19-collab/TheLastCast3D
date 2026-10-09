// Fishing, ported from the web game's server/fishing.js:
//
//   idle -> (hold to charge) -> casting -> waiting -> bite -> reeling -> catch | snap | escape
//
// Hold the left mouse button to charge a cast and release to throw; while you
// charge, a ghost bobber shows where it will land (green on water, red where it
// can't). When the bobber dips, click to hook. Then hold to reel and let go
// when the fish pulls, or the line snaps. Right-click (or R) reels your line in.
// You can walk (slowly) with a line out; wander too far and it comes in.
//
// At sea the water is the voyage's current stop (or the boss's waters), and a
// special event's (or boss attack's) mods change the bites, the fight and the
// points.

using UnityEngine;
using UnityEngine.InputSystem;

namespace LastCast
{
    public enum LineState { Idle, Casting, Waiting, Bite, Reeling }

    public class CatchInfo
    {
        public Species species;
        public float kg;
        public int points, coins, xp;
        public string zone;
        public bool hotspot, isNew;
        public string tier, trophyName;
        public int levelUp;
        public bool bonus; // the second fish of a Double Catch
    }

    public class Fishing : MonoBehaviour
    {
        public LineState State { get; private set; } = LineState.Idle;
        public float Charge { get; private set; } = -1; // power 0..1 while charging, else -1
        public float Tension { get; private set; }
        public float Progress { get; private set; }
        public bool Pulling { get; private set; }
        public int TierIndex { get; private set; }
        public bool Reeling { get; private set; }
        public bool GiantOn { get; private set; }

        PlayerController player;
        RodView rod;
        Vector3 castFrom, castTo; // Unity space
        float timer, chargeStart, fight, pullTimer, reelSpeed, lineStrength, drag;
        bool hotspot;
        Zone zone;
        Species fish;
        float fishKg;
        bool saved;
        Bonuses bonus = new Bonuses();
        GameObject ghost;
        MeshRenderer[] ghostParts;
        MaterialPropertyBlock ghostColour;

        public Vector3 BobberPos { get; private set; }

        public void Init(PlayerController p)
        {
            player = p;
            rod = RodView.Create(p.Cam.transform);
            BuildGhost();
        }

        Profile Profile => Game.I.Profile;
        static bool AtSea => Place.AtSea;

        void Update()
        {
            if (player == null) return;
            bool menu = Game.I.MenuOpen || AutoShot.Active || NetTest.Active;
            var mouse = Mouse.current;
            var kb = Keyboard.current;
            bool actionDown = !menu && (mouse?.leftButton.wasPressedThisFrame ?? false);
            bool actionHeld = !menu && (mouse?.leftButton.isPressed ?? false);
            bool actionUp = mouse?.leftButton.wasReleasedThisFrame ?? false;
            bool cancel = !menu && ((mouse?.rightButton.wasPressedThisFrame ?? false) || (kb?.rKey.wasPressedThisFrame ?? false));

            float dt = Time.deltaTime;
            switch (State)
            {
                case LineState.Idle when ForceCharge.HasValue:
                    Charge = ForceCharge.Value;
                    break;
                case LineState.Idle:
                    if (actionDown)
                    {
                        var blocked = Voyage.I?.CastBlocked;
                        if (blocked != null)
                        {
                            Game.I.Flash(blocked, 2.5f);
                            Sfx.Play("error");
                        }
                        else
                        {
                            chargeStart = Time.time;
                            Charge = 0;
                        }
                    }
                    if (Charge >= 0)
                    {
                        float t = (Time.time - chargeStart) / Tuning.ChargePeriod % 2;
                        Charge = t < 1 ? t : 2 - t; // ping-pong, so holding too long loses power
                        if (menu || Voyage.I?.CastBlocked != null) Charge = -1;
                        else if (actionUp) { TryCast(Charge); Charge = -1; }
                    }
                    break;
                case LineState.Casting:
                    timer -= dt;
                    float f = 1 - Mathf.Clamp01(timer / Tuning.CastFlightTime);
                    BobberPos = Vector3.Lerp(castFrom, castTo, f) + Vector3.up * Mathf.Sin(f * Mathf.PI) * Vector3.Distance(castFrom, castTo) * 0.35f;
                    if (timer <= 0) Land();
                    break;
                case LineState.Waiting:
                    timer -= dt;
                    BobberPos = castTo + Vector3.up * Mathf.Sin(Time.time * 2.2f) * 0.03f;
                    if (timer <= 0) Bite();
                    if (cancel) ReelIn("You reel your line in.");
                    break;
                case LineState.Bite:
                    timer -= dt;
                    BobberPos = castTo + Vector3.down * (0.12f + Mathf.Abs(Mathf.Sin(Time.time * 18)) * 0.08f);
                    if (actionDown) Hook();
                    else if (timer <= 0) { ResetLine(); Game.I.Flash("Too slow, the fish stole your bait."); }
                    else if (cancel) ReelIn("You reel your line in.");
                    break;
                case LineState.Reeling:
                    Reeling = actionHeld;
                    Fight(dt);
                    if (State == LineState.Reeling)
                    {
                        // The fish comes in from where it bit towards your feet.
                        var near = player.transform.position + player.Forward * 2f;
                        near.y = 0;
                        var p = Vector3.Lerp(castTo, near, Mathf.Clamp01(Progress));
                        float jerk = Pulling ? Mathf.Sin(Time.time * 30) * 0.25f : 0;
                        BobberPos = p + Vector3.down * 0.1f + player.transform.right * jerk;
                        if (cancel) ReelIn("You let the fish go.");
                    }
                    break;
            }
            // Walked off too far from your bobber: the line comes in.
            if (State == LineState.Waiting || State == LineState.Bite)
            {
                var me = player.transform.position;
                float reach = (Profile.Stats.castRange + Profile.Bonuses.castRange) / WorldMap.UnitsPerMetre * Tuning.LineSlack;
                if (new Vector2(me.x - castTo.x, me.z - castTo.z).magnitude > reach) ReelIn("You walked too far from your bobber and reeled it in.");
            }
            UpdateGhost(menu);
            rod.UpdateView(this, player, dt);
        }

        // ---- the line --------------------------------------------------------------------------

        /// <summary>Where a cast at this power lands, the water it lands in, or why it can't.</summary>
        (Vector3 target, Zone zone, string fail) Aim(float power)
        {
            float dist = Tuning.CastDistance(power, Profile.Stats.castRange + Profile.Bonuses.castRange) / WorldMap.UnitsPerMetre;
            var target = player.transform.position + player.Forward * dist;
            target.y = 0;
            if (AtSea)
            {
                var s = SeaWorld.ToSea(target);
                return SeaWorld.IsWater(s.x, s.y) ? (target, Voyage.I.Zone, null) : (target, null, GameData.I.voyage.castFail);
            }
            var w = WorldMap.I.ToWorld(target);
            var z = WorldMap.I.ZoneAt(w.x, w.y);
            return (target, z, z == null ? "Your cast landed on dry ground. Aim for the water." : null);
        }

        void TryCast(float power)
        {
            var (target, z, fail) = Aim(power);
            if (fail != null)
            {
                Game.I.Flash(fail);
                Sfx.Play("error");
                return;
            }
            zone = z;
            castFrom = rod.TipPosition;
            castTo = target;
            BobberPos = castFrom;
            timer = Tuning.CastFlightTime;
            State = LineState.Casting;
            Sfx.Play("cast");
        }

        void Land()
        {
            var stats = Profile.Stats;
            var w = WorldMap.I.ToWorld(castTo);
            var b = Profile.Bonuses;
            EventMods ev = null;
            SeaMods mods = null;
            float chum = 1, penalty = Tuning.CrowdPenalty;
            int crowd;
            if (AtSea)
            {
                var s = SeaWorld.ToSea(castTo);
                zone = Voyage.I.Zone;
                hotspot = Voyage.I.HotspotAt(s.x, s.y) != null;
                mods = Voyage.I.Mods;
                penalty = GameData.I.voyage.crowdPenalty; // a crowded deck is normal on a fishing boat
                crowd = Multiplayer.I.BobbersNear(w.x, w.y, Tuning.CrowdRadius);
                Voyage.I.OnLand(s.x, s.y); // a harpoon, if the boss is breaching there
            }
            else
            {
                hotspot = Game.I.HotspotAt(w.x, w.y) != null;
                ev = Game.I.Events.ModsAt(w.x, w.y);
                chum = Game.I.Chum.BiteBonus(w.x, w.y);
                // Other anglers' bobbers close by slow your bites (not in a Feeding Shoal, where company helps).
                crowd = ev?.ignoreCrowd == true ? 0 : Multiplayer.I.BobbersNear(w.x, w.y, Tuning.CrowdRadius);
            }
            float rate = zone.biteRate * stats.biteSpeed * (hotspot ? Tuning.HotspotBiteBoost : 1) * b.bite * b.ZoneBite(zone)
                * chum * (ev?.bite ?? 1) * (mods?.biteSpeed ?? 1) / (1 + penalty * crowd);
            State = LineState.Waiting;
            timer = (4 + Random.value * 8) / rate;
            BobberPos = castTo;
            Sfx.Play("plop");
            string spot = AtSea && hotspot && Voyage.I.Phase == VoyagePhase.Boss ? " (the weak spot! double damage)" : hotspot ? " (hotspot! rare fish more likely)" : "";
            Game.I.Flash($"Bobber landed in the {zone.name}{spot}" + (crowd > 0 ? $" (crowded: {crowd} line{(crowd > 1 ? "s" : "")} nearby, bites slower)" : ""), 2.5f);
        }

        void Bite()
        {
            var stats = Profile.Stats;
            var b = Profile.Bonuses;
            var w = WorldMap.I.ToWorld(castTo);
            float rare = 1;
            if (AtSea)
            {
                zone = Voyage.I.Zone; // a special event may have started (or ended) since the cast
                rare = Voyage.I.Mods?.rareBoost ?? 1;
            }
            else rare = Game.I.Events.ModsAt(w.x, w.y)?.rare ?? 1;
            fish = FishRules.Pick(zone, hotspot, stats, b, rare);
            // The fish took one bait, whether or not you hook it (unless your pet saved it).
            if (Random.value >= b.baitSave)
            {
                var outOf = Profile.UseBait();
                if (outOf != null) Game.I.Flash(outOf, 3);
            }
            fishKg = FishRules.RollKg(fish, b.weight);
            State = LineState.Bite;
            timer = Tuning.BiteWindow + b.biteWindow;
            Sfx.Play("bite");
        }

        void Hook()
        {
            var stats = Profile.Stats;
            var b = Profile.Bonuses;
            var mods = AtSea ? Voyage.I.Mods : null;
            GiantOn = Trophies.Tier(fish, fishKg) == "giant";
            fight = FishRules.Difficulty(fish, fishKg) * b.fight * (mods?.fight ?? 1) * (GiantOn ? Trophies.GiantFight : 1);
            Progress = 0.1f + 0.07f * Mathf.Min(fight, 2);
            Tension = 0;
            Pulling = false;
            pullTimer = 0.5f + Random.value;
            TierIndex = FishRules.TierOf(fight);
            reelSpeed = stats.reelSpeed * b.reel * (mods?.reelSpeed ?? 1);
            lineStrength = stats.lineStrength / (b.tension * (mods?.tension ?? 1));
            drag = stats.drag;
            saved = false;
            bonus = b;
            State = LineState.Reeling;
            Sfx.Play("hook");
            if (GiantOn) Game.I.Banner("A GIANT!", "Something enormous is on the line. Ease off when it runs!", Trophies.Tiers["giant"].color);
            else Game.I.Flash($"Hooked a {FishRules.Tiers[TierIndex].label.ToLower()} fish! Hold to reel, let go when it pulls.", 2);
        }

        /// <summary>The reeling mini-game (the same maths as the web game).</summary>
        void Fight(float dt)
        {
            float d = fight;
            pullTimer -= dt;
            if (pullTimer <= 0)
            {
                Pulling = !Pulling;
                pullTimer = Pulling ? 0.4f + Random.value * (0.5f + 0.45f * d) : (0.7f + Random.value * 1.3f) / (0.8f + 0.25f * d);
                if (Pulling) { Tension += 0.08f * Mathf.Max(0, d - 1) / lineStrength; Sfx.Play("splash"); }
            }
            if (Reeling)
            {
                Progress += dt * (Pulling ? 0.05f * reelSpeed - 0.03f * d : (0.18f / (0.6f + 0.4f * d) + 0.05f * Mathf.Max(0, d - 1)) * reelSpeed);
                Tension += dt * (Pulling ? 0.45f + 0.6f * d : 0.05f * (0.5f + 0.5f * d)) / lineStrength;
            }
            else
            {
                Progress -= dt * (Pulling ? 0.04f + 0.05f * d : 0.01f) * bonus.hold;
                Tension = Mathf.Max(0, Tension - dt * 0.7f * drag);
            }
            CheckLine();
            if (State != LineState.Reeling) return;
            if (Progress >= 1) LandCatch();
            else if (Progress <= 0)
            {
                ResetLine();
                Game.I.Flash("The fish shook free and got away.", 2.5f);
                Sfx.Play("error");
            }
        }

        /// <summary>Too much tension: the line snaps (unless armour or a pet saves it).</summary>
        void CheckLine()
        {
            if (Tension >= 1 && !saved && Random.value < bonus.snapSave)
            {
                // Stormbreaker armour (and some pets): the line holds, once per fight.
                saved = true;
                Tension = 0.55f;
                Game.I.Hud.Feed("Second Wind! Your line held on.", Skin.Accent);
            }
            if ((Tension >= 1 || Progress <= 0) && Random.value < bonus.rescue)
            {
                // Celestial Peacock and friends: the fish is caught anyway.
                Game.I.Hud.Feed("Second Life! Your pet saved the fish.", Skin.Hex("#c77dff"));
                Progress = 1;
                Tension = 0;
            }
            if (Tension >= 1)
            {
                Profile.snaps++;
                Profile.counters.snaps++;
                ResetLine();
                Game.I.Flash("SNAP! Too much tension. Let go when the fish pulls.", 3);
                Sfx.Play("snap");
            }
        }

        /// <summary>A jolt on the line (a boss thrashing, the boat lurching): only matters while you're fighting a fish.</summary>
        public void AddTension(float amount)
        {
            if (State != LineState.Reeling) return;
            Tension += amount / lineStrength;
            CheckLine();
        }

        /// <summary>Lines in: whatever's on the line gets away. With a message, it's shown.</summary>
        public void Cancel(string message)
        {
            Charge = -1;
            if (State == LineState.Idle) return;
            ResetLine();
            if (message != null) Game.I.Flash(message, 3);
        }

        void ReelIn(string message)
        {
            ResetLine();
            Game.I.Flash(message, 1.5f);
        }

        void ResetLine()
        {
            State = LineState.Idle;
            Tension = Progress = 0;
            Pulling = Reeling = GiantOn = false;
        }

        // ---- the ghost: where this cast will land --------------------------------------------------

        void BuildGhost()
        {
            ghost = new GameObject("Cast ghost");
            var ring = new MeshKit();
            int n = 28;
            for (int i = 0; i < n; i++)
            {
                if (i % 2 == 1) continue; // dashed
                float a0 = i / (float)n * Mathf.PI * 2, a1 = (i + 1) / (float)n * Mathf.PI * 2;
                var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
                var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                ring.Quad(d0 * 0.55f, d0 * 0.7f, d1 * 0.7f, d1 * 0.55f, new Color32(255, 255, 255, 220));
            }
            var fill = new MeshKit();
            for (int i = 0; i < n; i++)
            {
                float a0 = i / (float)n * Mathf.PI * 2, a1 = (i + 1) / (float)n * Mathf.PI * 2;
                fill.Tri(Vector3.zero, new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * 0.55f, new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * 0.55f, new Color32(255, 255, 255, 60));
            }
            // A see-through bobber standing in the middle.
            var bob = new MeshKit();
            bob.Cone(new Vector3(0, 0.05f, 0), 0.09f, 0.08f, 8, new Color32(255, 255, 255, 150), 0.09f);
            bob.Cone(new Vector3(0, 0.13f, 0), 0.09f, 0.1f, 8, new Color32(255, 255, 255, 150));
            bob.Cone(new Vector3(0, 0.23f, 0), 0.015f, 0.35f, 4, new Color32(255, 255, 255, 120), 0.015f);
            Mesh[] meshes = { ring.Build("ghost ring"), fill.Build("ghost fill"), bob.Build("ghost bobber") };
            ghostParts = new MeshRenderer[meshes.Length];
            for (int i = 0; i < meshes.Length; i++)
            {
                var part = new GameObject("Part");
                part.transform.SetParent(ghost.transform, false);
                part.AddComponent<MeshFilter>().sharedMesh = meshes[i];
                var r = part.AddComponent<MeshRenderer>();
                r.sharedMaterial = RodView.Unlit;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                ghostParts[i] = r;
            }
            ghostColour = new MaterialPropertyBlock();
            ghost.SetActive(false);
        }

        /// <summary>For AutoShot: hold a cast at this power (shows the ghost), or null.</summary>
        public static float? ForceCharge;

        void UpdateGhost(bool menu)
        {
            if (ForceCharge == null && Charge >= 0 && AutoShot.Active) Charge = -1;
            bool show = State == LineState.Idle && Charge >= 0 && (!menu || ForceCharge.HasValue);
            if (ghost.activeSelf != show) ghost.SetActive(show);
            if (!show) return;
            var (target, _, fail) = Aim(Charge);
            // Ride the waves a little, so it sits on the water rather than in it.
            float swell = AtSea ? 0.35f : 0.1f;
            ghost.transform.position = target + Vector3.up * (swell + Mathf.Sin(Time.time * 2.2f) * 0.03f);
            float pulse = 1 + Mathf.Sin(Time.time * 6) * 0.06f;
            float size = Mathf.Lerp(1.6f, 2.6f, Mathf.Clamp01(Vector3.Distance(player.transform.position, target) / 40f)); // stays easy to see far off
            ghost.transform.localScale = new Vector3(size * pulse, size, size * pulse);
            ghost.transform.rotation = Quaternion.Euler(0, Time.time * 40, 0);
            ghostColour.SetColor("_Color", fail == null ? new Color(0.55f, 1f, 0.6f, 1) : new Color(1f, 0.4f, 0.35f, 1));
            foreach (var r in ghostParts) r.SetPropertyBlock(ghostColour);
        }

        // ---- rewards -----------------------------------------------------------------------------

        void LandCatch()
        {
            var b = Profile.Bonuses;
            var s = fish;
            float kg = fishKg;
            ResetLine();
            var info = Reward(s, kg, b, false);
            // Armour set or pet: a second fish of the same kind on the line.
            CatchInfo extra = null;
            if (b.@double > 0 && Random.value < b.@double)
                extra = Reward(s, FishRules.RollKg(s, b.weight), b, true);
            Achievements.Check(Profile);
            Profile.Save();
            Sfx.Play(info.tier != null ? "trophy" : "catch");
            Game.I.OnCatch(info);
            if (extra != null)
            {
                Game.I.Banner("Double Catch!", $"Another {s.name} ({extra.kg} kg)", Skin.Green);
                Game.I.Hud.Feed($"Double Catch! A second {s.name} ({extra.kg} kg): +{extra.points} points, +{extra.coins} coins.", Skin.Green);
            }
            rod.ShowFish(s, kg, info.tier);
        }

        /// <summary>Points, coins, XP, Fish Index, trophies and counters for one landed fish (server/fishing.js rewardCatch).</summary>
        CatchInfo Reward(Species s, float kg, Bonuses b, bool bonusFish)
        {
            var p = Profile;
            var w = WorldMap.I.ToWorld(castTo);
            var ev = AtSea ? null : Game.I.Events.ModsAt(w.x, w.y);
            var mods = AtSea ? Voyage.I.Mods : null;
            string tier = Trophies.Tier(s, kg);
            float mult = (hotspot ? Tuning.HotspotScoreBonus * b.hotspot : 1) * (ev?.points ?? 1) * (mods?.points ?? 1) * (AtSea ? b.sea : 1)
                * (tier != null ? Trophies.Tiers[tier].points : 1);
            int points = FishRules.Score(s, kg, mult);
            int coins = Mathf.RoundToInt(points * Tuning.CoinsPerPoint * b.coins);
            // Pets that dig up coins (Field Mouse, Raccoon, Pirate Parrot).
            if (b.find != null && Random.value < b.find.chance)
            {
                int found = Random.Range(b.find.min, b.find.max + 1);
                coins += found;
                Game.I.Hud.Feed($"Your {GameData.I.PetById(b.pet)?.name ?? "pet"} found {found} coins!", Skin.Gold);
            }
            int xp = Mathf.RoundToInt(points * b.xp);
            int before = p.Level;

            p.score += points;
            p.coins += coins;
            p.catches++;
            p.heaviest = Mathf.Max(p.heaviest, kg);
            p.index.TryGetValue(s.id, out var entry);
            bool isNew = entry == null;
            entry ??= new IndexEntry { firstAt = Profile.Now };
            entry.count++;
            entry.bestKg = Mathf.Max(entry.bestKg, kg);
            if (tier != null) entry.trophies++;
            p.index[s.id] = entry;
            p.history.Insert(0, new CatchEntry { species = s.id, kg = kg, points = points, zone = zone.name, hotspot = hotspot, at = Profile.Now });
            if (p.history.Count > Profile.HistoryLimit) p.history.RemoveRange(Profile.HistoryLimit, p.history.Count - Profile.HistoryLimit);

            string trophyName = null;
            if (tier != null)
            {
                trophyName = Trophies.Name(tier, "player", s.id, kg, p.catches);
                p.trophies.Insert(0, new TrophyEntry { species = s.id, kg = kg, name = trophyName, tier = tier, zone = zone.name, at = Profile.Now });
                if (p.trophies.Count > Profile.CabinetLimit)
                {
                    int drop = p.trophies.FindLastIndex(t => t.tier != "giant");
                    p.trophies.RemoveAt(drop >= 0 ? drop : Profile.CabinetLimit);
                }
                p.trophyCount++;
                if (tier == "giant") p.giants++;
            }

            // Lifetime counters (achievements).
            var c = p.counters;
            c.catches++;
            c.coinsEarned += coins;
            if (hotspot) c.hotspotCatches++;
            if (s.rarity == "legendary") c.legendaryCatches++;
            if (s.rarity == "mythic") c.mythicCatches++;
            if (kg >= 10) c.bigFish++;
            c.heaviest = Mathf.Max(c.heaviest, kg);
            if (tier != null) c.trophies++;
            if (tier == "giant") c.giants++;
            Counters.Bump(c.family, s.family);
            Counters.Bump(c.zone, zone.kind ?? zone.id);
            if (AtSea)
            {
                c.seaCatches++;
                if (Voyage.I.EventOn) c.eventCatches++;
            }
            else
            {
                var here = Game.I.Player.WorldPos;
                var area = GameData.I.AreaAt(here.x, here.y);
                if (area != null) Counters.Bump(c.area, area.id);
            }

            p.xp += xp;
            int after = p.Level;
            int levelCoins = 0;
            for (int l = before + 1; l <= after; l++) levelCoins += Levels.LevelUpCoins(l);
            p.coins += levelCoins;
            c.coinsEarned += levelCoins;

            if (!AtSea)
            {
                Game.I.Chum.OnCatch(s.rarity);
                Game.I.Events.OnCatch(w.x, w.y, points);
            }
            return new CatchInfo
            {
                species = s, kg = kg, points = points, coins = coins, xp = xp, zone = zone.name, hotspot = hotspot, isNew = isNew,
                tier = tier, trophyName = trophyName, levelUp = after > before ? after : 0, bonus = bonusFish,
            };
        }
    }
}
