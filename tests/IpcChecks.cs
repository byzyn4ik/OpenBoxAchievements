using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace OpenBoxAchievements
{
    /// End-to-end check: the mod's test build hands telemetry to
    /// fh2-achievements.dll, which owns the rules (its own manifest, beside the
    /// DLL), evaluates them and sends results over the pipe to OpenBox's
    /// EventInbox + GameMessages. OpenBox has no rules. Works only in <root>.
    /// IpcChecks <root> <mod test DLL> <fh2-achievements.dll>
    static class IpcChecks
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        delegate int StartRules([MarshalAs(UnmanagedType.LPWStr)] string directory);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void Submit([MarshalAs(UnmanagedType.LPStr)] string json, uint length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int Flush(uint timeout);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void Stop(uint timeout);

        static void Check(bool condition, string error) { if (!condition) throw new Exception(error); }

        static T Export<T>(IntPtr module, string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            Check(address != IntPtr.Zero, "missing export " + name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        static bool WaitFor(Func<bool> condition, int milliseconds)
        {
            for (int waited = 0; waited < milliseconds; waited += 50)
            {
                if (condition()) return true;
                Thread.Sleep(50);
            }
            return condition();
        }

        static int Main(string[] args)
        {
            if (args[0] == "--next-launch") return NextLaunch(args);
            string root = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(root);
            typeof(Paths).GetField("Data").SetValue(null, Path.Combine(root, "OpenBoxData"));
            typeof(Paths).GetField("Games").SetValue(null, Path.Combine(root, "GameDefinitions"));
            EventInbox.PipeName = "OpenBoxAchievements.Events.v1.test-" + Guid.NewGuid().ToString("N");
            string folder = Path.Combine(Paths.Games, "fh2");
            Game.UpdateManifest(folder, info =>
            {
                info.Name = "FH2 test";
                info.Achievements = new[] { "xp", "boards", "laps", "legacy" };
            });
            // Before the DLL: two boards queued by the mod, an imported unlock.
            string board = "{\"e\":\"RewardBoardSmashed\",\"f\":[]}\n";
            Directory.CreateDirectory(Path.GetDirectoryName(Inbox));
            File.WriteAllText(Inbox, board + board);
            var state = new State();
            state.Unlocked["legacy"] = new Unlock { Time = "2026-01-01", Source = "xbox" };
            Game.Save(Path.Combine(Paths.LocalGame("fh2"), "state.json"), state);
            var game = Game.LoadAll().Single();
            var games = new List<Game> { game };

            // The mod's folder: FH2_ExtraOptions.ini and achievements\ with
            // fh2-achievements.dll and its rules (achievement_manifest.json).
            string mod = Path.Combine(root, "Mod");
            Directory.CreateDirectory(Path.Combine(mod, "achievements"));
            File.Copy(args[2], Path.Combine(mod, "achievements", "fh2-achievements.dll"), true);
            File.WriteAllText(Path.Combine(mod, "achievements", "achievement_manifest.json"),
                "{\"Name\":\"FH2 test\",\"Achievements\":[\"xp\",\"boards\",\"laps\",\"legacy\"],\"Rules\":[" +
                "{\"Achievement\":\"xp\",\"Event\":\"SaveStat\",\"Kind\":\"max\",\"Field\":4,\"Match\":{\"3\":\"CompareStats/WristbandLevel\"},\"Target\":100}," +
                "{\"Achievement\":\"boards\",\"Event\":\"RewardBoardSmashed\",\"Target\":2}," +
                "{\"Achievement\":\"laps\",\"Event\":\"Lap\",\"Target\":5}]}\n");
            File.WriteAllText(Path.Combine(mod, "FH2_ExtraOptions.ini"),
                "[Achievements]\r\nEnabled=1\r\nGame=fh2\r\nData=" + Paths.Data + "\r\nPipe=" + EventInbox.PipeName + "\r\n");

            var toasts = new List<(Game, Achievement)>();
            var received = new List<string>();
            int answered = 0;
            using (var inbox = new EventInbox(lines =>
            {
                lock (games)
                {
                    received.AddRange(lines.Where(l => l.Trim().Length > 0));
                    bool ok = GameMessages.Apply(games, lines, new HashSet<Game>(), toasts);
                    if (ok) answered++;
                    return ok;
                }
            }))
            {
                IntPtr module = LoadLibrary(Path.GetFullPath(args[1]));
                Check(module != IntPtr.Zero, "Cannot load the mod's test DLL");
                var start = Export<StartRules>(module, "Fh2TestStartAchievementRules");
                var submit = Export<Submit>(module, "Fh2TestSubmitAchievementTelemetry");
                IntPtr rules = LoadLibrary(Path.Combine(mod, "achievements", "fh2-achievements.dll"));
                var flush = Export<Flush>(rules, "Fh2AchFlush");
                var stop = Export<Stop>(rules, "Fh2AchStop");
                Check(start(mod) == 1, "The mod did not start achievements\\fh2-achievements.dll");

                Check(WaitFor(() => { lock (games) return toasts.Any(t => t.Item2.Id == "boards"); }, 8000),
                    "The queued boards did not unlock 'boards' in game (rules beside the DLL)");
                lock (games) Check(received.Any(l => l.StartsWith("hello fh2 1 fh2-achievements/")), "No hello");
                string ingame = File.ReadAllText(Paths.GameState("fh2"));
                Check(ingame.Contains("\"boards|RewardBoardSmashed|count|-1\":2") && ingame.Contains("\"legacy\":{\"Time\":\"2026-01-01\",\"Source\":\"xbox\""),
                    "ingame_state.json does not hold the replayed progress: " + ingame);
                Console.WriteLine("PASS: the DLL loads the rules beside it, replays queued telemetry and keeps OpenBox's unlocks.");

                string snapshot = "{\"e\":\"SaveStat\",\"f\":[null,null,null,{\"t\":0,\"v\":\"CompareStats/WristbandLevel\"},{\"t\":5,\"v\":90}]}\r\n";
                string gain = "{\"e\":\"XPGained\",\"f\":[null,null,null,{\"t\":5,\"v\":15}]}\r\n";
                string lap = "{\"e\":\"Lap\",\"f\":[]}\r\n";
                string batch = snapshot + gain + lap + lap;
                submit(batch, (uint)Encoding.UTF8.GetByteCount(batch));
                Check(flush(3000) == 1, "Flush was not acknowledged by OpenBox");
                lock (games)
                {
                    Check(toasts.Count(t => t.Item2.Id == "xp") == 1, "XP unlock was not decided in game and sent once");
                    Check(game.Results.Progress(new Achievement { Id = "laps" }, out string text) == 0.4, "Progress 2/5 not shown: " + text);
                    Check(received.Any(l => l.StartsWith("unlocked fh2 xp ")) && received.Any(l => l == "progress fh2 laps 2/5"), "Messages: " + string.Join(" | ", received));
                }
                Console.WriteLine("PASS: in-game rules decide unlocks (XP total 90+15), OpenBox gets 'unlocked' and 'progress 2/5' and answers 'ok'.");

                // A resend (e.g. the game did not get the answer) is recorded once.
                using (var client = new NamedPipeClientStream(".", EventInbox.PipeName, PipeDirection.InOut))
                {
                    client.Connect(3000);
                    byte[] again = Encoding.ASCII.GetBytes("hello fh2 1 test\nunlocked fh2 xp 2026-01-01T00:00:00Z\nend\n");
                    client.Write(again, 0, again.Length);
                    byte[] reply = new byte[8];
                    int n = client.Read(reply, 0, reply.Length);
                    Check(Encoding.ASCII.GetString(reply, 0, n) == "ok\n", "No 'ok' answer");
                }
                lock (games) Check(toasts.Count(t => t.Item2.Id == "xp") == 1 && game.State.Unlocked.Count == 3, "A resent unlock was counted twice");
                Console.WriteLine("PASS: a resent unlock is not recorded or shown twice.");

                stop(3000);
                string saved = File.ReadAllText(Paths.GameState("fh2"));
                Check(!saved.Contains("\"Notified\":false"), "Acknowledged unlocks are not marked as notified");
                Check(saved.Contains("\"telemetry|fh2|total-xp\":105") && saved.Contains("\"laps|Lap|count|-1\":2"), "In-game counters not saved: " + saved);
                Console.WriteLine("PASS: ingame_state.json keeps counters and acknowledged unlocks.");
            }

            // The page after a restart: saved progress, unlocks from ingame_state.json.
            game = Game.LoadAll().Single();
            Check(game.Results.Poll().Count == 0, "Restart showed unlocks again");
            Check(game.Results.Progress(new Achievement { Id = "laps" }, out _) == 0.4, "Restarted OpenBox does not show the saved progress");
            Check(game.State.Unlocked.Count == 3 && game.State.Unlocked["legacy"].Source == "xbox", "Existing unlocks were not preserved");
            Check(!Directory.GetFiles(folder, "*state.json", SearchOption.AllDirectories).Any(), "State leaked into a game definition folder");
            Console.WriteLine("PASS: a restarted OpenBox shows the in-game state; nothing replays.");

            // The DLL is one per process: the next launch is another process.
            var next = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                System.Reflection.Assembly.GetExecutingAssembly().Location,
                "--next-launch \"" + root + "\" \"" + Path.GetFullPath(args[1]) + "\" \"" + EventInbox.PipeName + "\"")
                { UseShellExecute = false });
            next.WaitForExit();
            Check(next.ExitCode == 0, "Next launch checks failed");
            return 0;
        }

        /// A launch without the rules DLL (the mod queues telemetry; OpenBox
        /// does nothing with it), then the DLL back: it replays what it missed
        /// once; nothing counts twice.
        static int NextLaunch(string[] args)
        {
            string root = args[1];
            typeof(Paths).GetField("Data").SetValue(null, Path.Combine(root, "OpenBoxData"));
            typeof(Paths).GetField("Games").SetValue(null, Path.Combine(root, "GameDefinitions"));
            EventInbox.PipeName = args[3];
            string mod = Path.Combine(root, "Mod");
            var game = Game.LoadAll().Single();
            var games = new List<Game> { game };
            var toasts = new List<(Game, Achievement)>();
            using (var inbox = new EventInbox(lines =>
            {
                lock (games) return GameMessages.Apply(games, lines, new HashSet<Game>(), toasts);
            }))
            {
                IntPtr module = LoadLibrary(args[2]);
                Check(module != IntPtr.Zero, "Cannot load the mod's test DLL");
                var send = (SendRaw)Marshal.GetDelegateForFunctionPointer(GetProcAddress(module, "Fh2TestSendOpenBoxTelemetry"), typeof(SendRaw));
                Thread.Sleep(300);
                string lap = "{\"e\":\"Lap\",\"f\":[]}\n";
                // No game id: no notification on the real pipe name (a running OpenBox); the test polls.
                send(Inbox, "", lap, (uint)lap.Length);
                send(Inbox, "", lap, (uint)lap.Length);
                lock (games) game.Results.Poll();
                lock (games) Check(game.Results.Progress(new Achievement { Id = "laps" }, out _) == 0.4,
                    "Without the DLL, OpenBox changed the progress itself");
                Console.WriteLine("PASS: without fh2-achievements.dll the mod queues telemetry; OpenBox does not evaluate it.");

                var start = Export<StartRules>(module, "Fh2TestStartAchievementRules");
                var submit = Export<Submit>(module, "Fh2TestSubmitAchievementTelemetry");
                Check(start(mod) == 1, "The rules DLL did not start");
                IntPtr rules = LoadLibrary(Path.Combine(mod, "achievements", "fh2-achievements.dll"));
                var flush = Export<Flush>(rules, "Fh2AchFlush");
                Check(flush(5000) == 1, "No acknowledged sync");
                lock (games)
                {
                    Check(game.Results.Progress(new Achievement { Id = "laps" }, out string text) == 0.8, "Caught-up progress is not 4/5: " + text);
                }
                submit(lap, (uint)lap.Length);
                Check(flush(5000) == 1, "No acknowledged unlock");
                lock (games) Check(toasts.Count(t => t.Item2.Id == "laps") == 1 && game.State.Unlocked.ContainsKey("laps"),
                    "Laps (2 in game + 2 caught up + 1) did not unlock exactly once");
                Export<Stop>(rules, "Fh2AchStop")(3000);
                string log = File.ReadAllText(Path.Combine(mod, "achievements", "fh2-achievements.log"));
                Check(log.Contains("replayed 2 raw telemetry lines written without the DLL"), "Catch-up not logged: " + log);
                Console.WriteLine("PASS: the DLL back replays the 2 lines it missed once (4/5), then unlocks at 5/5; OpenBox shows it once.");
            }
            return 0;
        }

        /// The mod's telemetry queue (the DLL replays what it missed from it).
        static string Inbox => Path.Combine(Paths.Data, "inbox", "fh2.jsonl");

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        delegate void SendRaw([MarshalAs(UnmanagedType.LPWStr)] string path,
            [MarshalAs(UnmanagedType.LPStr)] string game, [MarshalAs(UnmanagedType.LPStr)] string json, uint length);
    }
}
