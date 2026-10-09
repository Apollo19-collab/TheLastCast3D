// Updates the game from its GitHub repo (GameVersion.Repo), from inside the game.
//
// On start it asks GitHub for the repo's latest release. If that's newer than
// this copy, the title screen, a toast and the Options tab offer "Update now":
// it downloads the release's zip (checking its size, and its SHA-256 when GitHub
// gives one), then hands over to a small PowerShell script and quits. The
// script waits for the game to close, unpacks the zip over the game folder and
// starts the game again. Your save lives elsewhere (AppData\LocalLow), so it's
// never touched.

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace LastCast
{
    public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

    public class Updater : MonoBehaviour
    {
        public static Updater I { get; private set; }
        public UpdateState State { get; private set; } = UpdateState.Idle;
        public string Latest { get; private set; } // e.g. "1.1.0"
        public string Notes { get; private set; } // the release's description
        public string Error { get; private set; }
        public float Progress { get; private set; }

        string downloadUrl, sha256;
        long size;

        /// <summary>Only a built game can replace its own files (not the Unity editor), and never in test runs.</summary>
        public static bool Enabled => !Application.isEditor && !AutoShot.Active && !NetTest.Active && !GameVersion.Repo.StartsWith("OWNER/");

        public static void Create(GameObject host)
        {
            I = host.AddComponent<Updater>();
            // Just updated? Say so.
            string last = PlayerPrefs.GetString("version", "");
            if (last != "" && last != GameVersion.Current) Game.I.Hud.Feed($"Updated to version {GameVersion.Current}!", Skin.Green);
            PlayerPrefs.SetString("version", GameVersion.Current);
            if (Enabled) I.Check();
        }

        public void Check()
        {
            if (State == UpdateState.Checking || State == UpdateState.Downloading || State == UpdateState.Installing) return;
            StartCoroutine(CheckRoutine());
        }

        static UnityWebRequest Get(string url)
        {
            var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("User-Agent", "TheLastCast-Updater");
            req.SetRequestHeader("Accept", "application/vnd.github+json");
            req.timeout = 20;
            return req;
        }

        IEnumerator CheckRoutine()
        {
            State = UpdateState.Checking;
            Error = null;
            using var req = Get($"https://api.github.com/repos/{GameVersion.Repo}/releases/latest");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                // 404: no releases yet. Anything else: offline, or GitHub is busy. Either way, just carry on.
                State = req.responseCode == 404 ? UpdateState.UpToDate : UpdateState.Failed;
                Error = req.responseCode == 404 ? null : $"Couldn't check for updates ({req.error}).";
                yield break;
            }
            try
            {
                var rel = JObject.Parse(req.downloadHandler.text);
                Latest = ((string)rel["tag_name"] ?? "").TrimStart('v', 'V');
                Notes = ((string)rel["body"] ?? "").Trim();
                downloadUrl = null;
                foreach (var a in rel["assets"] ?? new JArray())
                {
                    if ((string)a["name"] != GameVersion.ZipName) continue;
                    downloadUrl = (string)a["browser_download_url"];
                    size = (long?)a["size"] ?? 0;
                    var digest = (string)a["digest"]; // "sha256:...", when GitHub provides it
                    sha256 = digest != null && digest.StartsWith("sha256:") ? digest.Substring(7).ToLowerInvariant() : null;
                }
                bool newer = Newer(Latest, GameVersion.Current);
                if (newer && downloadUrl == null)
                {
                    State = UpdateState.Failed;
                    Error = $"Version {Latest} is out, but its release has no {GameVersion.ZipName} to download.";
                }
                else if (newer)
                {
                    State = UpdateState.Available;
                    Game.I.Hud.Toast("globe", "Update available", $"Version {Latest}", "Install it from the Options tab (O)");
                    Game.I.Hud.Feed($"Version {Latest} of The Last Cast is out! Open Options (O) and choose Update now.", Skin.Accent);
                }
                else State = UpdateState.UpToDate;
            }
            catch (Exception e)
            {
                State = UpdateState.Failed;
                Error = $"Couldn't read the update information: {e.Message}";
            }
        }

        /// <summary>Is version a newer than b? ("1.10.0" > "1.9.2")</summary>
        public static bool Newer(string a, string b) =>
            Version.TryParse(Pad(a), out var va) && Version.TryParse(Pad(b), out var vb) && va > vb;

        static string Pad(string v)
        {
            var parts = v.Split('.');
            return parts.Length == 1 ? v + ".0" : v;
        }

        public void Install()
        {
            if (State != UpdateState.Available) return;
            StartCoroutine(InstallRoutine());
        }

        IEnumerator InstallRoutine()
        {
            // Only ever download from GitHub itself.
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com")
            {
                Fail("The update's download link doesn't point at GitHub, so it wasn't downloaded.");
                yield break;
            }
            State = UpdateState.Downloading;
            Progress = 0;
            string dir = Path.Combine(Path.GetTempPath(), "TheLastCastUpdate");
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                Fail($"Couldn't make a folder for the download: {e.Message}");
                yield break;
            }
            string zip = Path.Combine(dir, GameVersion.ZipName);
            using (var req = UnityWebRequest.Get(downloadUrl))
            {
                req.SetRequestHeader("User-Agent", "TheLastCast-Updater");
                req.downloadHandler = new DownloadHandlerFile(zip) { removeFileOnAbort = true };
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    Progress = req.downloadProgress;
                    yield return null;
                }
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Fail($"The download failed ({req.error}). Try again in a moment.");
                    yield break;
                }
            }
            Progress = 1;

            // Check it's the file GitHub said it would be.
            var info = new FileInfo(zip);
            if (size > 0 && info.Length != size)
            {
                Fail("The download was incomplete. Try again.");
                yield break;
            }
            if (sha256 != null)
            {
                string got;
                using (var s = File.OpenRead(zip))
                using (var h = SHA256.Create()) got = BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
                if (got != sha256)
                {
                    Fail("The download didn't match GitHub's checksum, so it wasn't installed.");
                    yield break;
                }
            }

            // Hand over to the script, which finishes once the game has closed.
            State = UpdateState.Installing;
            try
            {
                string script = Path.Combine(dir, "update.ps1");
                File.WriteAllText(script, Script);
                string gameDir = Path.GetDirectoryName(Application.dataPath);
                string exe = Path.Combine(gameDir, "TheLastCast.exe");
                var psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -GamePid {Process.GetCurrentProcess().Id} -Zip \"{zip}\" -Target \"{gameDir}\" -Exe \"{exe}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(psi);
            }
            catch (Exception e)
            {
                Fail($"Couldn't start the installer: {e.Message}");
                yield break;
            }
            Game.I.SaveNow();
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        void Fail(string message)
        {
            State = UpdateState.Failed;
            Error = message;
            Debug.LogWarning(message);
            Game.I.Hud.Feed(message, Skin.Red);
        }

        // Waits for the game to close, unpacks the update over the game folder, and starts it again.
        // If anything goes wrong it leaves the old files in place and writes why to update.log.
        const string Script = @"param([int]$GamePid, [string]$Zip, [string]$Target, [string]$Exe)
$ErrorActionPreference = 'Stop'
$dir = Split-Path $Zip
$log = Join-Path $dir 'update.log'
try {
    try { Wait-Process -Id $GamePid -Timeout 60 } catch { }
    Start-Sleep -Milliseconds 500
    $stage = Join-Path $dir 'files'
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    Expand-Archive -Path $Zip -DestinationPath $stage -Force
    # The zip may hold the game's files directly, or inside one folder.
    $src = $stage
    $items = @(Get-ChildItem $stage)
    if ($items.Count -eq 1 -and $items[0].PSIsContainer) { $src = $items[0].FullName }
    if (-not (Test-Path (Join-Path $src 'TheLastCast.exe'))) { throw 'The update has no TheLastCast.exe in it.' }
    robocopy $src $Target /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw ""Copying the new files failed (robocopy $LASTEXITCODE)."" }
    ""Updated at $(Get-Date)"" | Out-File $log
} catch {
    ""Update failed at $(Get-Date): $_"" | Out-File $log
}
Start-Process $Exe
";
    }
}
