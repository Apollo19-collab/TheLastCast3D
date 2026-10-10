// The game's version, and the GitHub repo its updates come from. Make-Release.ps1
// sets Current when it publishes a release (tagged v<Current>, with the game
// zipped as TheLastCast-Windows.zip); the in-game updater (Net/Updater.cs)
// compares it with the repo's latest release.

namespace LastCast
{
    public static class GameVersion
    {
        public const string Current = "1.1.1";
        public const string Repo = "Apollo19-collab/TheLastCast3D";
        public const string ZipName = "TheLastCast-Windows.zip";
    }
}
