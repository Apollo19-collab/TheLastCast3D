// Multiplayer: one player hosts from inside the game; friends join with the
// short join code (or pick the game from the list of open games). Up to 8
// anglers. Connections go through Unity's online service, so nobody needs to
// open ports on their router.
//
// What's shared: where everyone is and what their line is doing, catches and
// chat, the hotspots (the host decides them), chum buckets, map events
// (co-op goals, sized for everyone online, with rewards for everyone who helps)
// and boat voyages (everyone aboard the same boat shares the voyage; see Voyage.cs).
// Each player keeps their own progress, tackle and pets on their own PC.
//
// Messages are JSON, sent as Netcode named messages. Clients send to the host;
// the host stamps the sender's id and passes them on to everyone else.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace LastCast
{
    public enum NetMode { Offline, Busy, Host, Client }

    /// <summary>What another angler is doing, ~12 times a second.</summary>
    public class AnglerState
    {
        public ulong id;
        public string n; // name
        public int lv, col;
        public float x, y, z, yaw, pitch;
        public int ls; // LineState
        public float bx, by, bz, tn;
        public string pet, rod, hat; // armour set for the hat and jacket look
        public int c; // catches this session
        public float best; // heaviest this session
        public long vy; // the voyage they're on (or booked on, aboard at the dock): its departure time; 0 on the lake
        public float ch = -1; // how far a cast is charged (0..1), or -1 when not charging
        public bool sp; // sprinting
    }

    public class Multiplayer : MonoBehaviour
    {
        public const int MaxPlayers = 8;
        const string SessionType = "lastcast";
        const string StateMsg = "lc.state", ReliableMsg = "lc.msg";
        const float StateInterval = 1 / 12f;

        public static Multiplayer I { get; private set; }
        public NetMode Mode { get; private set; } = NetMode.Offline;
        public bool Online => Mode == NetMode.Host || Mode == NetMode.Client;
        public bool IsHost => Mode == NetMode.Host;
        public bool IsClient => Mode == NetMode.Client;
        public string Status { get; private set; } = "";
        public ISession Session { get; private set; }
        public ulong MyId => NetworkManager.Singleton ? NetworkManager.Singleton.LocalClientId : 0;
        public readonly Dictionary<ulong, RemoteAngler> Remotes = new Dictionary<ulong, RemoteAngler>();
        public int SessionCatches;
        public float SessionBest;
        public event Action<string, JObject, ulong> Received; // (type, message, sender)

        NetworkManager nm;
        float stateTimer;
        bool handlersRegistered;
        Task<bool> signIn;

        public static void Create()
        {
            if (I) return;
            var go = new GameObject("Multiplayer");
            DontDestroyOnLoad(go);
            I = go.AddComponent<Multiplayer>();
        }

        // ---- services ------------------------------------------------------------------------------

        async Task<bool> SignIn()
        {
            try
            {
                if (UnityServices.State != ServicesInitializationState.Initialized)
                {
                    // A fresh sign-in profile each launch, so two copies of the game on one PC are two players.
                    var options = new InitializationOptions().SetProfile("p" + UnityEngine.Random.Range(0, int.MaxValue).ToString("x"));
                    await UnityServices.InitializeAsync(options);
                }
                var auth = AuthenticationService.Instance;
                if (!auth.IsSignedIn)
                {
                    try { await auth.SignInAnonymouslyAsync(); }
                    catch (AuthenticationException) when (!auth.IsSignedIn)
                    {
                        // Already signing in (e.g. started by the services themselves): wait for it.
                        for (int i = 0; i < 100 && !auth.IsSignedIn; i++) await Task.Delay(100);
                        if (!auth.IsSignedIn) throw;
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                Status = $"Couldn't reach the online service: {e.Message}";
                Debug.LogException(e);
                return false;
            }
        }

        /// <summary>Sign in once; later calls share the same sign-in (or retry one that failed).</summary>
        Task<bool> EnsureSignedIn()
        {
            if (signIn == null || (signIn.IsCompleted && !signIn.Result)) signIn = SignIn();
            return signIn;
        }

        void EnsureNetworkManager()
        {
            if (nm) return;
            var go = new GameObject("NetworkManager");
            DontDestroyOnLoad(go);
            go.SetActive(false);
            nm = go.AddComponent<NetworkManager>();
            var transport = go.AddComponent<UnityTransport>();
            nm.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = transport,
                ConnectionApproval = false,
                EnableSceneManagement = false,
                TickRate = 30,
            };
            go.SetActive(true); // NetworkManager.Singleton is set when it's enabled
            nm.OnServerStarted += RegisterHandlers;
            nm.OnClientStarted += RegisterHandlers;
            nm.OnClientConnectedCallback += OnClientConnected;
            nm.OnClientDisconnectCallback += OnClientDisconnected;
        }

        // ---- hosting and joining ---------------------------------------------------------------------

        public async Task<bool> Host(string gameName, bool isPrivate)
        {
            if (Mode != NetMode.Offline) return false;
            Mode = NetMode.Busy;
            Status = "Connecting to the online service...";
            if (!await EnsureSignedIn()) { Mode = NetMode.Offline; return false; }
            EnsureNetworkManager();
            try
            {
                Status = "Opening your lake to other anglers...";
                var options = new SessionOptions
                {
                    MaxPlayers = MaxPlayers,
                    Name = string.IsNullOrWhiteSpace(gameName) ? $"{PlayerName}'s lake" : gameName.Trim(),
                    IsPrivate = isPrivate,
                    Type = SessionType,
                }.WithRelayNetwork();
                Session = await MultiplayerService.Instance.CreateSessionAsync(options);
                Mode = NetMode.Host;
                Status = "";
                Watch(Session);
                Game.I.Hud.Feed($"You're hosting \"{Session.Name}\". Friends can join with the code {Session.Code}.", Skin.Accent);
                Game.I.OnBecameHost();
                return true;
            }
            catch (Exception e)
            {
                Fail("Couldn't host a game", e);
                return false;
            }
        }

        public async Task<bool> Join(string code = null, string sessionId = null)
        {
            if (Mode != NetMode.Offline) return false;
            Mode = NetMode.Busy;
            Status = "Connecting to the online service...";
            if (!await EnsureSignedIn()) { Mode = NetMode.Offline; return false; }
            EnsureNetworkManager();
            try
            {
                Status = "Joining...";
                var options = new JoinSessionOptions { Type = SessionType };
                Session = sessionId != null
                    ? await MultiplayerService.Instance.JoinSessionByIdAsync(sessionId, options)
                    : await MultiplayerService.Instance.JoinSessionByCodeAsync(code.Trim().ToUpperInvariant(), options);
                Mode = NetMode.Client;
                Status = "";
                Watch(Session);
                Game.I.Hud.Feed($"You joined \"{Session.Name}\"! Everyone's fishing the same lake now.", Skin.Accent);
                Game.I.OnJoinedGame();
                return true;
            }
            catch (Exception e)
            {
                Fail("Couldn't join that game", e);
                return false;
            }
        }

        /// <summary>The open (public) games anyone can join.</summary>
        public async Task<List<ISessionInfo>> Browse()
        {
            if (!await EnsureSignedIn()) return new List<ISessionInfo>();
            try
            {
                var results = await MultiplayerService.Instance.QuerySessionsAsync(new QuerySessionsOptions
                {
                    FilterOptions = new List<FilterOption> { new FilterOption(FilterField.AvailableSlots, "0", FilterOperation.Greater) },
                });
                return results.Sessions.ToList();
            }
            catch (Exception e)
            {
                Status = $"Couldn't load the list of games: {e.Message}";
                return new List<ISessionInfo>();
            }
        }

        public async Task Leave()
        {
            if (!Online) return;
            var s = Session;
            Mode = NetMode.Busy;
            try { if (s != null) await s.LeaveAsync(); }
            catch (Exception e) { Debug.LogException(e); }
            EndSession("You left the game. You're fishing on your own now.");
        }

        void Watch(ISession s)
        {
            s.RemovedFromSession += () => EndSession("You were removed from the game.");
            s.Deleted += () => EndSession("The host closed the game. You're fishing on your own now.");
            SessionCatches = 0;
            SessionBest = 0;
        }

        void Fail(string what, Exception e)
        {
            Debug.LogException(e);
            Mode = NetMode.Offline;
            Session = null;
            string why = e is SessionException se ? se.Message : e.Message;
            Status = $"{what}: {why}";
            Game.I.Hud.Feed(Status, Skin.Red);
            if (nm && nm.IsListening) nm.Shutdown();
        }

        void EndSession(string message)
        {
            if (Mode == NetMode.Offline) return;
            Mode = NetMode.Offline;
            Session = null;
            handlersRegistered = false;
            foreach (var r in Remotes.Values) if (r) Destroy(r.gameObject);
            Remotes.Clear();
            if (nm && nm.IsListening) nm.Shutdown();
            Game.I?.Hud?.Feed(message, Skin.Gold);
            Game.I?.OnLeftGame();
        }

        static string playerName;

        /// <summary>Your name as other anglers see it (remembered between games).</summary>
        public static string PlayerName
        {
            get => playerName ??= PlayerPrefs.GetString("name", Environment.UserName);
            set
            {
                var v = string.IsNullOrWhiteSpace(value) ? Environment.UserName : value.Trim();
                v = v.Substring(0, Math.Min(16, v.Length));
                if (v == playerName) return;
                playerName = v;
                if (!NetTest.Active) PlayerPrefs.SetString("name", v);
            }
        }

        // ---- connections ----------------------------------------------------------------------------

        void OnClientConnected(ulong id)
        {
            if (!IsHost || id == NetworkManager.ServerClientId) return;
            Game.I.OnPlayerJoined(id);
        }

        void OnClientDisconnected(ulong id)
        {
            if (IsClient && id == MyId)
            {
                EndSession("You lost the connection to the host. You're fishing on your own now.");
                return;
            }
            if (Remotes.TryGetValue(id, out var r))
            {
                Game.I.Hud.Feed($"{r.State?.n ?? "An angler"} packed up and left.", Skin.Dim);
                Destroy(r.gameObject);
                Remotes.Remove(id);
            }
            if (IsHost) SendReliable("left", new JObject { ["who"] = id });
        }

        // ---- messages ------------------------------------------------------------------------------

        void RegisterHandlers()
        {
            if (handlersRegistered || nm.CustomMessagingManager == null) return;
            handlersRegistered = true;
            nm.CustomMessagingManager.RegisterNamedMessageHandler(StateMsg, OnStateMessage);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(ReliableMsg, OnReliableMessage);
        }

        static FastBufferWriter Writer(string json)
        {
            var w = new FastBufferWriter(1024, Allocator.Temp, 1 << 18);
            w.WriteValueSafe(json);
            return w;
        }

        /// <summary>Everyone else connected (the host's view).</summary>
        List<ulong> Others(ulong except) => nm.ConnectedClientsIds.Where(c => c != NetworkManager.ServerClientId && c != except).ToList();

        void Send(string channel, string json, NetworkDelivery delivery, ulong? to = null, ulong except = ulong.MaxValue)
        {
            if (!Online || nm == null || !nm.IsListening || nm.CustomMessagingManager == null) return;
            using var w = Writer(json);
            if (IsClient) nm.CustomMessagingManager.SendNamedMessage(channel, NetworkManager.ServerClientId, w, delivery);
            else if (to.HasValue) nm.CustomMessagingManager.SendNamedMessage(channel, to.Value, w, delivery);
            else
            {
                var targets = Others(except);
                if (targets.Count > 0) nm.CustomMessagingManager.SendNamedMessage(channel, targets, w, delivery);
            }
        }

        /// <summary>Send a message: from a client it goes to the host (who passes it on); from the host to everyone (or one player).</summary>
        public void SendReliable(string type, JObject body, ulong? to = null)
        {
            body["t"] = type;
            body["from"] = MyId;
            Send(ReliableMsg, body.ToString(Formatting.None), NetworkDelivery.ReliableFragmentedSequenced, to);
        }

        void OnStateMessage(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out string json);
            var st = JsonConvert.DeserializeObject<AnglerState>(json);
            if (IsHost)
            {
                st.id = sender; // the host vouches for who sent it
                Send(StateMsg, JsonConvert.SerializeObject(st), NetworkDelivery.UnreliableSequenced, except: sender);
            }
            if (st.id == MyId) return;
            if (!Remotes.TryGetValue(st.id, out var r) || !r)
            {
                r = RemoteAngler.Create(st.id);
                Remotes[st.id] = r;
                Game.I.Hud.Feed($"{st.n} arrived at the lake.", Skin.Accent);
            }
            r.Apply(st);
        }

        void OnReliableMessage(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out string json);
            var msg = JObject.Parse(json);
            if (IsHost)
            {
                msg["from"] = sender;
                // Pass it on, unless it's meant for the host alone.
                string t = (string)msg["t"];
                if (t != "evcatch") Send(ReliableMsg, msg.ToString(Formatting.None), NetworkDelivery.ReliableFragmentedSequenced, except: sender);
            }
            ulong from = msg["from"]?.Value<ulong>() ?? sender;
            if (from == MyId && !IsHost) return;
            Received?.Invoke((string)msg["t"], msg, from);
        }

        // ---- my state, ~12 times a second --------------------------------------------------------------

        void Update()
        {
            if (!Online || Game.I == null || Game.I.Player == null) return;
            stateTimer -= Time.deltaTime;
            if (stateTimer > 0) return;
            stateTimer = StateInterval;
            var p = Game.I.Player;
            var f = Game.I.Fishing;
            var prof = Game.I.Profile;
            var pos = p.transform.position;
            prof.armour.TryGetValue("body", out var body);
            var st = new AnglerState
            {
                id = MyId, n = PlayerName, lv = prof.Level, col = (int)(MyId % 10),
                x = pos.x, y = pos.y, z = pos.z, yaw = p.Yaw, pitch = p.Pitch,
                ls = (int)f.State, bx = f.BobberPos.x, by = f.BobberPos.y, bz = f.BobberPos.z, tn = f.Tension,
                pet = prof.pet, rod = prof.equipped["rod"], hat = body != null ? GameData.I.Piece(body).set?.id : null,
                c = SessionCatches, best = SessionBest,
                vy = Voyage.I == null ? 0 : Voyage.I.AtSea ? Voyage.I.Id : Voyage.I.Boarded,
                ch = f.Charge, sp = p.Sprinting,
            };
            Send(StateMsg, JsonConvert.SerializeObject(st), NetworkDelivery.UnreliableSequenced);
        }

        void OnApplicationQuit()
        {
            if (Online && Session != null) _ = Session.LeaveAsync();
        }
    }
}
