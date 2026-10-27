using System;
using System.Collections.Generic;
using Windows.Data.Json;

namespace OpenBoxWidget
{
    /// What OpenBox Achievements writes to this package's LocalState as
    /// summary.json (a desktop app can write there; the widget cannot read
    /// OpenBox's own folder):
    /// {"id":"fh2","lang":"en"|"uk","game":..,"boxart":"boxart.jpg","unlocked":n,"total":n,
    ///  "earned":n,"gamerscore":n,"achievements":[{"id","name","description",
    ///  "gamerscore","unlocked","time","progress":0..1,"text","rare"}]}
    /// The box art is in LocalState\art.
    sealed class Summary
    {
        public string Id = "", Lang = "", Game = "", BoxArt = "";
        public bool Active;
        public int Unlocked, Total, Earned, Gamerscore;
        public List<Achievement> Achievements = new List<Achievement>();

        public sealed class Achievement
        {
            public string Id = "", Name = "", Description = "", Time = "", Text = "";
            public int Gamerscore;
            public bool IsUnlocked, Rare;
            public double Progress;
            public DateTimeOffset Unlocked => DateTimeOffset.TryParse(Time, out var t) ? t : DateTimeOffset.MinValue;
        }

        public static Summary Parse(string text)
        {
            var json = JsonObject.Parse(text);
            var summary = new Summary
            {
                Id = String(json, "id"), Lang = String(json, "lang"), Game = String(json, "game"), BoxArt = String(json, "boxart"), Active = Bool(json, "active"),
                Unlocked = (int)Number(json, "unlocked"), Total = (int)Number(json, "total"),
                Earned = (int)Number(json, "earned"), Gamerscore = (int)Number(json, "gamerscore"),
            };
            if (json.ContainsKey("achievements") && json["achievements"].ValueType == JsonValueType.Array)
                foreach (var value in json["achievements"].GetArray())
                {
                    if (value.ValueType != JsonValueType.Object) continue;
                    var o = value.GetObject();
                    summary.Achievements.Add(new Achievement
                    {
                        Id = String(o, "id"), Name = String(o, "name"), Description = String(o, "description"),
                        Time = String(o, "time"), Text = String(o, "text"), Gamerscore = (int)Number(o, "gamerscore"),
                        IsUnlocked = Bool(o, "unlocked"), Rare = Bool(o, "rare"), Progress = Number(o, "progress"),
                    });
                }
            return summary;
        }

        static string String(JsonObject o, string key) =>
            o.ContainsKey(key) && o[key].ValueType == JsonValueType.String ? o[key].GetString() : "";

        static double Number(JsonObject o, string key) =>
            o.ContainsKey(key) && o[key].ValueType == JsonValueType.Number ? o[key].GetNumber() : 0;

        static bool Bool(JsonObject o, string key) =>
            o.ContainsKey(key) && o[key].ValueType == JsonValueType.Boolean && o[key].GetBoolean();
    }
}
