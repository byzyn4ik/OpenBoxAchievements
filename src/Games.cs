using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace OpenBoxAchievements
{
    /// achievement_manifest.json: everything shareable about a game, in one
    /// file under games\<id>\ beside the app (the folder name is the id).
    /// See docs\MANIFEST.md.
    sealed class GameInfo
    {
        public string Name;
        public uint XboxTitleId;            // 0 for a manual game
        public string Process;              // the EXE name without .exe, for a game without a mod on the pipe: time played, the page over the game
        public string[] Achievements = new string[0]; // the achievement ids

        [ScriptIgnore] public string Id;    // the folder name
        [ScriptIgnore] public string Source => XboxTitleId != 0 ? "xbox" : "manual";
    }

    sealed class Achievement
    {
        public string Id, Name, Description, LockedDescription, Icon; // Icon: image file name beside the data
        public bool IsSecret;
        public int Gamerscore;
        public double RarityPercent;
    }

    sealed class Unlock
    {
        public string Time;   // ISO 8601, UTC
        public string Source; // "local" or "xbox" (imported)
    }

    /// achievement_data.json: names, texts and art file names. Loaded from
    /// Xbox it is the user's local copy (app data); a manual game's own data
    /// may sit beside its manifest.
    sealed class Catalog
    {
        public string TitleName;
        public List<Achievement> Achievements = new List<Achievement>();
    }

    sealed class State
    {
        public Dictionary<string, Unlock> Unlocked = new Dictionary<string, Unlock>();
        // Progress the game's mod sent: achievement id -> [current, target].
        public Dictionary<string, double[]> Progress = new Dictionary<string, double[]>();
        public double SecondsPlayed;
        public string GameStateStamp; // the mod's ingame_state.json last read
    }

    /// Where things live. The app's folder holds what is shared (the EXE and
    /// games\<id>\achievement_manifest.json); everything personal or loaded
    /// from Xbox is in %LOCALAPPDATA%\OpenBoxAchievements: openbox.ini,
    /// account.bin, error.log and games\<id>\ with achievement_data.json, the
    /// art and state.json. Achievement rules are not OpenBox's: each game's
    /// mod decides unlocks and sends them over the pipe (EventInbox).
    static class Paths
    {
        public static readonly string App = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string Games = Path.Combine(App, "games");
        public static string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenBoxAchievements");
        public static string LocalGame(string id) => Path.Combine(Data, "games", id);
        /// The mod's own state (FH2: fh2-achievements.dll writes it, OpenBox
        /// only reads its unlocks).
        public static string GameState(string id) => Path.Combine(Data, "games", id, "ingame_state.json");

        /// Files of earlier versions beside the EXE move to app data once.
        public static void MoveOldFiles()
        {
            Directory.CreateDirectory(Data);
            foreach (var name in new[] { "openbox.ini", "account.bin", "error.log" })
            {
                string from = Path.Combine(App, name), to = Path.Combine(Data, name);
                try { if (File.Exists(from) && !File.Exists(to)) File.Move(from, to); } catch (IOException) { }
            }
        }
    }

    sealed class Game
    {
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        public const string ManifestFile = "achievement_manifest.json", DataFile = "achievement_data.json",
            ImportedUnlocksFile = "xbox_unlocks.json";

        /// Folder: the shared one (manifest). Local: app data (data, art, state).
        public readonly string Folder, Local;
        public readonly GameInfo Info;
        public Catalog Catalog;
        public State State;
        public readonly Results Results;

        Game(string folder, GameInfo info)
        {
            Folder = folder;
            Info = info;
            Local = Paths.LocalGame(info.Id);
            Catalog = Load<Catalog>(Path.Combine(Local, DataFile)) ?? Load<Catalog>(Path.Combine(Folder, DataFile)) ?? new Catalog();
            State = Load<State>(Path.Combine(Local, "state.json")) ?? new State();
            MergeImportedUnlocks();
            Results = new Results(this);
        }

        public string ManifestPath => Path.Combine(Folder, ManifestFile);
        public string Name => !string.IsNullOrEmpty(Info.Name) ? Info.Name : Catalog.TitleName ?? Info.Id;

        /// A shared manifest whose texts and art are still to be loaded (an
        /// Xbox game's manifest may not list its IDs yet; loading adds them).
        public bool NeedsData => Catalog.Achievements.Count == 0 && (Info.Achievements.Length > 0 || Info.XboxTitleId != 0);

        /// Every folder under games with a manifest (older layouts are converted).
        public static List<Game> LoadAll()
        {
            Directory.CreateDirectory(Paths.Games);
            var list = new List<Game>();
            foreach (var folder in Directory.GetDirectories(Paths.Games).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try { Upgrade(folder); } catch (Exception error) { Log.Write("upgrade " + folder + ": " + error.Message); }
                var info = ReadManifest(folder);
                if (info == null) continue;
                // A copied folder (same Xbox title) is loaded once.
                if (info.XboxTitleId != 0 && list.Any(g => g.Info.XboxTitleId == info.XboxTitleId)) continue;
                list.Add(new Game(folder, info));
            }
            return list;
        }

        public static GameInfo ReadManifest(string folder)
        {
            var info = Load<GameInfo>(Path.Combine(folder, ManifestFile));
            if (info == null) return null;
            info.Id = Path.GetFileName(folder);
            info.Achievements = info.Achievements ?? new string[0];
            return info;
        }

        /// Changes the manifest of a folder (created if missing).
        public static void UpdateManifest(string folder, Action<GameInfo> change)
        {
            Directory.CreateDirectory(folder);
            var info = ReadManifest(folder) ?? new GameInfo { Id = Path.GetFileName(folder) };
            change(info);
            WriteManifest(folder, info);
        }

        /// The manifest, readable in a diff: one field per line.
        static void WriteManifest(string folder, GameInfo info)
        {
            string S(string value) => json.Serialize(value ?? "");
            var text = new StringBuilder();
            text.Append("{\n");
            text.Append("  \"Name\": ").Append(S(info.Name)).Append(",\n");
            text.Append("  \"XboxTitleId\": ").Append(info.XboxTitleId.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            text.Append("  \"Process\": ").Append(S(info.Process)).Append(",\n");
            text.Append("  \"Achievements\": [").Append(string.Join(", ", info.Achievements.Select(S))).Append("]\n");
            text.Append("}\n");
            string path = Path.Combine(folder, ManifestFile), temporary = path + ".tmp";
            File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }

        /// Earlier layouts: game.json + achievement_ids.json (+ rules.json, no
        /// longer used), with data, art and state beside them. The manifest
        /// takes the first two; the rest moves to app data (a manual game's
        /// data and art stay, they are the user's own).
        static void Upgrade(string folder)
        {
            string old = Path.Combine(folder, "game.json");
            if (!File.Exists(old)) return;
            var previous = Load<Dictionary<string, object>>(old) ?? new Dictionary<string, object>();
            string Field(string key) => previous.TryGetValue(key, out var v) ? v as string ?? "" : "";
            uint titleId = previous.TryGetValue("XboxTitleId", out var t) ? Convert.ToUInt32(t, CultureInfo.InvariantCulture) : 0;
            var data = Load<Catalog>(Path.Combine(folder, DataFile)) ?? Load<Catalog>(Path.Combine(folder, "catalog.json"));
            var ids = Load<string[]>(Path.Combine(folder, "achievement_ids.json")) ?? data?.Achievements.Select(a => a.Id).ToArray() ?? new string[0];
            if (!File.Exists(Path.Combine(folder, ManifestFile)))
                UpdateManifest(folder, info =>
                {
                    info.Name = Field("Name"); info.XboxTitleId = titleId;
                    info.Process = Field("Process");
                    info.Achievements = ids;
                });
            string local = Paths.LocalGame(Path.GetFileName(folder));
            Directory.CreateDirectory(local);
            bool fromXbox = titleId != 0 && Field("Source") != "manual";
            foreach (var file in Directory.GetFiles(folder))
            {
                string name = Path.GetFileName(file), ext = Path.GetExtension(file).ToLowerInvariant();
                bool personal = name == "state.json" || name == ImportedUnlocksFile;
                bool xboxContent = fromXbox && (name == DataFile || name == "catalog.json" || ext == ".jpg" || ext == ".png");
                if (!personal && !xboxContent) continue;
                string target = Path.Combine(local, name == "catalog.json" ? DataFile : name);
                if (File.Exists(target)) File.Delete(file); else File.Move(file, target);
            }
            string art = Path.Combine(folder, "art");
            if (fromXbox && Directory.Exists(art))
            {
                foreach (var file in Directory.GetFiles(art))
                {
                    string target = Path.Combine(local, Path.GetFileName(file));
                    if (File.Exists(target)) File.Delete(file); else File.Move(file, target);
                }
                Directory.Delete(art);
            }
            foreach (var name in new[] { "game.json", "achievement_ids.json", "rules.json", ".gitignore" })
                if (File.Exists(Path.Combine(folder, name))) File.Delete(Path.Combine(folder, name));
        }

        static T Load<T>(string path) where T : class
        {
            try { return File.Exists(path) ? json.Deserialize<T>(File.ReadAllText(path)) : null; }
            catch (Exception) { return null; }
        }

        public static void Save(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, json.Serialize(value));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }

        /// The data (app data, or beside the manifest for a manual game's own
        /// texts) and the ids in the manifest.
        public static void SaveCatalog(string folder, Catalog catalog, bool shared)
        {
            Save(Path.Combine(shared ? folder : Paths.LocalGame(Path.GetFileName(folder)), DataFile), catalog);
            UpdateManifest(folder, info => info.Achievements = catalog.Achievements.Select(a => a.Id).ToArray());
        }

        public void SaveState() => Save(Path.Combine(Local, "state.json"), State);

        /// xbox_unlocks.json, written by an import with "Obtain progress from
        /// the account": added to state.json (unlocks made here are kept), then
        /// removed. A separate file, so the running app's own save of
        /// state.json cannot overwrite it.
        void MergeImportedUnlocks()
        {
            string file = Path.Combine(Local, ImportedUnlocksFile);
            var imported = Load<Dictionary<string, Unlock>>(file);
            if (imported == null) return;
            foreach (var unlock in imported)
                if (!State.Unlocked.ContainsKey(unlock.Key)) State.Unlocked[unlock.Key] = unlock.Value;
            SaveState();
            try { File.Delete(file); } catch (IOException) { }
        }

        public bool IsUnlocked(Achievement a) => State.Unlocked.ContainsKey(a.Id);
        public int Earned => Catalog.Achievements.Where(IsUnlocked).Sum(a => a.Gamerscore);
        public int Total => Catalog.Achievements.Sum(a => a.Gamerscore);
        public int UnlockedCount => Catalog.Achievements.Count(IsUnlocked);
        public bool Completed => Catalog.Achievements.Count > 0 && UnlockedCount == Catalog.Achievements.Count;

        public string Art(Achievement a) => (string.IsNullOrEmpty(a.Icon) ? null : FindFile(a.Icon)) ?? Image(a.Id);
        public string BoxArt => Image("boxart");
        /// background.jpg, or (Settings: "Use the first achievement's art as
        /// the background") the first achievement's art.
        public string Background => Image("background") ??
            (UseAchievementArt ? Catalog.Achievements.Select(Art).FirstOrDefault(path => path != null) : null);

        public static bool UseAchievementArt;

        /// A file in app data, else beside the manifest.
        string FindFile(string name)
        {
            foreach (var folder in new[] { Local, Folder })
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        string Image(string name) => FindFile(name + ".jpg") ?? FindFile(name + ".png");

        /// Being played: its mod is connected over the pipe (Bus), or, for a
        /// game whose manifest names its EXE (Process), that EXE is running.
        public bool IsRunning => Bus.Connected(Info.Id) || (!string.IsNullOrEmpty(Info.Process) &&
            System.Diagnostics.Process.GetProcessesByName(Info.Process).Length > 0);
    }
}
