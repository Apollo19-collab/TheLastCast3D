// Game updates in the HUD: a strip under the title screen and a section in the
// Options tab, both showing the version and offering "Update now" when GitHub
// has a newer release (Net/Updater.cs).

using UnityEngine;

namespace LastCast
{
    public partial class Hud
    {
        string UpdateText(out Color colour)
        {
            var u = Updater.I;
            colour = Skin.Dim;
            if (u == null || !Updater.Enabled) return $"Version {GameVersion.Current}";
            switch (u.State)
            {
                case UpdateState.Checking: return $"Version {GameVersion.Current} · checking for updates...";
                case UpdateState.UpToDate: return $"Version {GameVersion.Current} · up to date";
                case UpdateState.Available: colour = Skin.Gold; return $"Version {u.Latest} is available! (you have {GameVersion.Current})";
                case UpdateState.Downloading: colour = Skin.Accent; return $"Downloading version {u.Latest}... {Mathf.RoundToInt(u.Progress * 100)}%";
                case UpdateState.Installing: colour = Skin.Accent; return "Installing: the game will restart in a moment...";
                case UpdateState.Failed: colour = Skin.Hex("#ffb4a2"); return u.Error ?? "Couldn't check for updates.";
                default: return $"Version {GameVersion.Current}";
            }
        }

        /// <summary>Under the title screen's window.</summary>
        void DrawUpdateStrip(Rect under)
        {
            var u = Updater.I;
            string s = UpdateText(out var colour);
            bool canInstall = u != null && u.State == UpdateState.Available;
            var st = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
            float bw = canInstall ? S(120) : 0;
            float w = Mathf.Min(st.CalcSize(new GUIContent(s)).x + S(32) + bw, Screen.width - S(32));
            var r = new Rect(under.center.x - w / 2, under.yMax + S(10), w, S(34));
            GUI.Box(r, GUIContent.none, pill);
            Label(new Rect(r.x + S(12), r.y, r.width - bw - S(20), r.height), s, new GUIStyle(st) { clipping = TextClipping.Clip }, colour);
            if (canInstall && GUI.Button(new Rect(r.xMax - bw - S(6), r.y + S(4), bw, r.height - S(8)), "Update now", buy)) u.Install();
            if (u != null && u.State == UpdateState.Downloading) Bar(new Rect(r.x + S(14), r.yMax - S(5), r.width - S(28), S(3)), u.Progress, Skin.Accent2, Skin.Accent);
        }

        /// <summary>In the Options tab.</summary>
        void DrawUpdateOptions()
        {
            var u = Updater.I;
            GUILayout.Label("UPDATES", new GUIStyle(smallDim) { fontStyle = FontStyle.Bold });
            GUILayout.BeginVertical(row);
            string s = UpdateText(out var colour);
            GUILayout.Label(s, new GUIStyle(text) { normal = { textColor = colour } });
            if (u != null && Updater.Enabled)
            {
                if (u.State == UpdateState.Available && !string.IsNullOrEmpty(u.Notes))
                    GUILayout.Label(u.Notes.Length > 400 ? u.Notes.Substring(0, 400) + "..." : u.Notes, smallDim);
                if (u.State == UpdateState.Downloading)
                    Bar(GUILayoutUtility.GetRect(S(200), S(8)), u.Progress, Skin.Accent2, Skin.Accent);
                GUILayout.BeginHorizontal();
                if (u.State == UpdateState.Available && GUILayout.Button("Update now (the game restarts)", buy, GUILayout.Height(S(32)))) u.Install();
                if ((u.State == UpdateState.UpToDate || u.State == UpdateState.Failed || u.State == UpdateState.Idle) && GUILayout.Button("Check for updates", button, GUILayout.Height(S(32)))) u.Check();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Label($"Updates come from github.com/{GameVersion.Repo}. Your save isn't touched.", smallDim);
            }
            GUILayout.EndVertical();
            GUILayout.Space(S(10));
        }
    }
}
