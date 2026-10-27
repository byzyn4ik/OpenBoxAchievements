using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace OpenBoxAchievements
{
    /// Messages of a game's achievement mod over the pipe (EventInbox;
    /// docs\EVENTS.md), one per line:
    ///   hello <game> <protocol> <producer>
    ///   unlocked <game> <id> [<ISO 8601 UTC time>]
    ///   progress <game> <id> <current>/<target>
    ///   end
    /// Ids and times are percent-encoded. An unlock is recorded once (a resend
    /// is ignored), so nothing is counted or shown twice.
    static class GameMessages
    {
        /// Applies them (UI thread). changed: games whose state changed;
        /// toasts: new unlocks. True when the message was complete ("end").
        public static bool Apply(IEnumerable<Game> games, string[] lines, HashSet<Game> changed, List<(Game, Achievement)> toasts)
        {
            bool understood = false;
            foreach (var raw in lines)
            {
                string[] words = raw.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;
                if (words[0] == "end") { understood = true; continue; }
                if (words.Length < 2) continue;
                var game = games.FirstOrDefault(g => string.Equals(g.Info.Id, words[1], StringComparison.OrdinalIgnoreCase));
                if (game == null) continue;
                string id = words.Length > 2 ? Uri.UnescapeDataString(words[2]) : null;
                switch (words[0])
                {
                    case "hello":
                        Log.Write(game.Info.Id + ": connected: " + string.Join(" ", words.Skip(2)));
                        break;
                    case "unlocked" when id != null:
                        var achievement = game.Results.Unlocked(id, words.Length > 3 ? Uri.UnescapeDataString(words[3]) : null);
                        if (achievement != null)
                        {
                            Log.Write(game.Info.Id + ": achievement " + achievement.Id + " unlocked in game");
                            toasts.Add((game, achievement));
                        }
                        changed.Add(game);
                        break;
                    case "progress" when id != null && words.Length > 3:
                        string[] parts = words[3].Split('/');
                        if (parts.Length == 2 &&
                            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double current) &&
                            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double target))
                        {
                            game.Results.Progressed(id, current, target);
                            changed.Add(game);
                        }
                        break;
                }
            }
            foreach (var game in changed) game.SaveState();
            return understood;
        }
    }

    /// A game's achievement results as its mod decides them: OpenBox has no
    /// rules of its own. Unlocks and progress come over the pipe
    /// (GameMessages) and are kept in State; unlocks made while OpenBox was
    /// closed are read from the mod's ingame_state.json.
    sealed class Results
    {
        readonly Game store;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public Results(Game store) => this.store = store;

        /// Progress 0..1 for the viewer, or -1 without progress.
        public double Progress(Achievement achievement, out string text)
        {
            text = null;
            if (!store.State.Progress.TryGetValue(achievement.Id, out var p) || p.Length != 2 || p[1] <= 1) return -1;
            text = Math.Min(p[0], p[1]).ToString("N0", CultureInfo.CurrentCulture) + " / " + p[1].ToString("N0", CultureInfo.CurrentCulture);
            return Math.Min(1, p[0] / p[1]);
        }

        Achievement Find(string id) =>
            store.Catalog.Achievements.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) ??
            (store.Info.Achievements.Contains(id) ? new Achievement { Id = id, Name = id } : null);

        /// "unlocked" from the game: recorded once. The achievement when it is new.
        public Achievement Unlocked(string id, string time)
        {
            var achievement = Find(id);
            if (achievement == null || store.State.Unlocked.ContainsKey(achievement.Id)) return null;
            if (!DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _))
                time = DateTime.UtcNow.ToString("o");
            store.State.Unlocked[achievement.Id] = new Unlock { Time = time, Source = "local" };
            store.State.Progress.Remove(achievement.Id);
            Changed = true;
            return achievement;
        }

        /// "progress" from the game.
        public void Progressed(string id, double current, double target)
        {
            if (target <= 0 || double.IsNaN(current) || double.IsNaN(target)) return;
            store.State.Progress[id] = new[] { current, target };
            Changed = true;
        }

        /// ingame_state.json, when it changed: its unlocks are recorded (those
        /// the game has not had acknowledged are new: they are returned, for
        /// the toast).
        void ReadGameState(List<Achievement> unlocked)
        {
            string path = Paths.GameState(store.Info.Id);
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            string stamp = info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + info.Length.ToString(CultureInfo.InvariantCulture);
            if (stamp == store.State.GameStateStamp) return;
            Dictionary<string, object> state;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    state = json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
            }
            catch (Exception error) when (error is IOException || error is ArgumentException || error is InvalidOperationException || error is UnauthorizedAccessException)
            {
                return; // being replaced: read on the next tick
            }
            if (state == null) return;
            store.State.GameStateStamp = stamp;
            if (state.TryGetValue("Unlocked", out var u) && u is Dictionary<string, object> unlocks)
                foreach (var entry in unlocks)
                {
                    var unlock = entry.Value as Dictionary<string, object>;
                    string time = unlock != null && unlock.TryGetValue("Time", out var t) ? t as string : null;
                    bool notified = unlock == null || !unlock.TryGetValue("Notified", out var n) || !(n is bool b) || b;
                    var achievement = Unlocked(entry.Key, time);
                    if (achievement != null && !notified) unlocked.Add(achievement);
                }
            Changed = true;
        }

        /// True when the last Poll changed anything.
        public bool Changed { get; private set; }

        /// Newly unlocked achievements since the last call.
        public List<Achievement> Poll()
        {
            Changed = false;
            var unlocked = new List<Achievement>();
            ReadGameState(unlocked);
            if (Changed) store.SaveState();
            return unlocked;
        }
    }
}
