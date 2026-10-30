using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace OpenBoxAchievements
{
    /// Feeds the OpenBox Xbox Game Bar widget (src\OpenBoxWidget). The widget
    /// is a sandboxed package and cannot read our folder, but we can write to
    /// its own LocalState: summary.json (every achievement of the game) and
    /// the box art (art\boxart.jpg). Its "All achievements" button opens
    /// openbox-achievements:show?game=<id>, which brings up our page.
    /// Nothing happens when the widget is not installed.
    static class Widget
    {
        const string PackagePrefix = "OpenBoxAchievements.Widget_";
        static readonly JavaScriptSerializer json = new JavaScriptSerializer();
        static string lastText;

        /// Where Windows keeps the packages' data (tests: a folder of their own).
        public static string Packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");

        static string Folder()
        {
            string packages = Packages;
            if (!Directory.Exists(packages)) return null;
            string package = Directory.GetDirectories(packages, PackagePrefix + "*").FirstOrDefault();
            return package == null ? null : Path.Combine(package, "LocalState");
        }

        /// active: the game is being played; otherwise the widget shows "No active game".
        public static void Write(Game game, bool active)
        {
            if (game == null) return;
            string folder;
            try { folder = Folder(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
            if (folder == null) return;
            try
            {
                string art = Path.Combine(folder, "art");
                Directory.CreateDirectory(art);
                // Every achievement, as the native widget lists them: the
                // widget filters (all, locked, unlocked, rare) and sorts.
                var list = game.Catalog.Achievements.Select(a =>
                {
                    bool unlocked = game.IsUnlocked(a), hidden = a.IsSecret && !unlocked;
                    string text = "";
                    double progress = unlocked ? 1 : Math.Max(0, game.Results.Progress(a, out text));
                    if (unlocked) text = "";
                    return new Dictionary<string, object>
                    {
                        ["id"] = a.Id,
                        ["name"] = hidden ? Lang.T("Secret achievement", "Секретне досягнення") : a.Name,
                        ["description"] = hidden ? Lang.T("Keep playing to find out.", "Грайте далі, щоб дізнатися.") :unlocked ? a.Description : (a.LockedDescription ?? a.Description),
                        ["gamerscore"] = a.Gamerscore,
                        ["unlocked"] = unlocked,
                        ["time"] = unlocked ? game.State.Unlocked[a.Id].Time : "",
                        ["progress"] = progress,
                        ["text"] = text ?? "",
                        ["rare"] = a.RarityPercent > 0 && a.RarityPercent < 10,
                    };
                }).ToList();
                string boxart = "";
                if (game.BoxArt != null && File.Exists(game.BoxArt))
                {
                    boxart = "boxart" + Path.GetExtension(game.BoxArt);
                    string copy = Path.Combine(art, boxart);
                    if (!File.Exists(copy) || File.GetLastWriteTimeUtc(copy) < File.GetLastWriteTimeUtc(game.BoxArt)) File.Copy(game.BoxArt, copy, true);
                }
                var summary = new Dictionary<string, object>
                {
                    ["id"] = game.Info.Id,
                    ["lang"] = Lang.Code, // the widget's own texts follow the app's language
                    ["active"] = active,
                    ["game"] = game.Name,
                    ["boxart"] = boxart,
                    ["unlocked"] = game.UnlockedCount,
                    ["total"] = game.Catalog.Achievements.Count,
                    ["earned"] = game.Earned,
                    ["gamerscore"] = game.Total,
                    ["achievements"] = list,
                };
                string text = json.Serialize(summary);
                if (text == lastText) return; // the widget rereads only when the file changes
                string path = Path.Combine(folder, "summary.json"), temporary = path + ".tmp";
                File.WriteAllText(temporary, text);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                lastText = text;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
