using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace OpenBoxAchievements.Tests
{
    /// Which game is being played, end to end: the built app runs headless in
    /// a sandbox (--sandbox: its own data, widget folder, pipe and instance
    /// names; the user's OpenBox and data are not touched), fake game mods talk
    /// to its pipe, and the checks read what the Game Bar widget would show
    /// (summary.json: active, id) and the time played (state.json).
    ///   BusChecks <work folder with app\OpenBox Achievements.exe> [<plugin_test.exe> <OpenBoxAchievements.dll>]
    /// The optional pair runs the real FH2 plugin DLL (its own test host,
    /// plugin_test bus) against the app.
    static class BusChecks
    {
        static string root, exe, pipe, summary;
        static int failures;
        static readonly JavaScriptSerializer json = new JavaScriptSerializer();

        static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--idle") { Thread.Sleep(int.Parse(args[1]) * 1000); return 0; }
            root = Path.GetFullPath(args[0]).TrimEnd('\\');
            exe = Path.Combine(root, "app", "OpenBox Achievements.exe");
            pipe = "OpenBoxAchievements.Events.v1." + Path.GetFileName(root);
            string widget = Path.Combine(root, "Packages", "OpenBoxAchievements.Widget_test", "LocalState");
            Directory.CreateDirectory(widget);
            summary = Path.Combine(widget, "summary.json");
            SetUpGames();
            try { Run(args); }
            catch (Exception error) { Fail("unexpected: " + error); }
            finally { Exit(); }
            Console.WriteLine(failures == 0 ? "all bus checks passed" : failures + " failure(s)");
            return failures == 0 ? 0 : 1;
        }

        /// The real FH2 manifest (no Process: FH2 is known only by its plugin on
        /// the pipe), a game known by its EXE, and one more game.
        static void SetUpGames()
        {
            string games = Path.Combine(root, "app", "games");
            foreach (var folder in Directory.GetDirectories(games))
                if (!string.Equals(Path.GetFileName(folder), "fh2", StringComparison.OrdinalIgnoreCase)) Directory.Delete(folder, true);
            var fh2 = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(games, "fh2", "achievement_manifest.json")));
            Check(!fh2.ContainsKey("Process") || string.IsNullOrEmpty(fh2["Process"] as string), "the FH2 manifest names no process (FH2 is known by its plugin)");
            Manifest("othergame", "Other game", "");
            Manifest("procgame", "Process game", ProcessGame);
        }

        static string ProcessGame => "obx-procgame-" + Path.GetFileName(root).Substring(0, 12);

        static void Manifest(string id, string name, string process)
        {
            Directory.CreateDirectory(Path.Combine(root, "app", "games", id));
            File.WriteAllText(Path.Combine(root, "app", "games", id, "achievement_manifest.json"),
                "{\"Name\":\"" + name + "\",\"XboxTitleId\":0,\"Process\":\"" + process + "\",\"Achievements\":[\"1\",\"2\"]}");
        }

        static void Run(string[] args)
        {
            Console.WriteLine("app: " + exe);
            StartApp(4);
            Check(WaitFor(() => File.Exists(summary), 8000), "the app writes the widget's summary.json");
            Check(!Active(), "nothing played: the widget shows no active game");

            Section("a process named \"Forza Horizon 2\" alone does not make FH2 active");
            using (var fake = Idle("Forza Horizon 2", 60))
            {
                Check(Always(() => !Active(), 5000), "\"Forza Horizon 2.exe\" running, no plugin: no active game");
                fake.Kill();
            }

            Section("a game whose manifest names its EXE (Process) is active while it runs");
            double procStart = Played("procgame");
            using (var game = Idle(ProcessGame, 60))
            {
                Check(WaitFor(() => Active() && Id() == "procgame", 6000), "procgame running: active, procgame");
                Thread.Sleep(2000);
                game.Kill();
            }
            Check(WaitFor(() => !Active(), 6000), "procgame exited: no active game");
            Check(WaitFor(() => Played("procgame") > procStart + 1, 3000), "procgame's time played was counted and saved when it ended");

            Section("FH2 is active while its mod is on the bus");
            int self = Process.GetCurrentProcess().Id;
            Check(Send("hello fh2 1 bus-checks/1 pid=" + self), "hello answered ok");
            Check(WaitFor(() => Active() && Id() == "fh2", 4000), "after hello: active, fh2");
            var clock = Stopwatch.StartNew();
            bool held = true, answered = true;
            while (clock.ElapsedMilliseconds < 8000) // twice the app's bus timeout
            {
                answered &= Send("alive fh2 pid=" + self);
                Thread.Sleep(1000);
                held &= Active() && Id() == "fh2";
            }
            Check(answered, "every alive (heartbeat) answered ok");
            Check(held, "heartbeats keep FH2 active past the timeout (8 s, timeout 4 s)");
            Check(Send("bye fh2"), "bye answered ok");
            Check(WaitFor(() => !Active(), 4000), "after bye: no active game");
            double fh2Played = 0;
            Check(WaitFor(() => (fh2Played = Played("fh2")) > 0, 3000), "FH2's time played was saved when it ended");
            Check(fh2Played >= 6 && fh2Played <= 16, "FH2's time played matches the session (" + fh2Played.ToString("0.0") + " s, about 9 s)");

            Section("another game id does not make FH2 active");
            Check(Send("hello nosuchgame 1 bus-checks/1"), "an unknown game is answered ok");
            Check(Always(() => !Active(), 2500), "an unknown game id: no active game");
            Check(Send("hello othergame 1 bus-checks/1 pid=" + self), "othergame hello answered ok");
            Check(WaitFor(() => Active() && Id() == "othergame", 4000), "othergame on the bus: active, othergame (not fh2)");
            Check(Send("bye othergame"), "othergame bye answered ok");
            Check(WaitFor(() => !Active(), 4000), "othergame gone: no active game");

            Section("time played grows only while active");
            Thread.Sleep(4000);
            Exit();
            Check(Math.Abs(Played("fh2") - fh2Played) < 0.01, "FH2's time played did not grow while it was not active (" + Played("fh2").ToString("0.0") + " s)");
            Check(Played("othergame") > 0, "othergame's time played was counted");

            StartApp(4);
            Section("a mod that stops talking ends after the timeout");
            Check(Send("hello fh2 1 bus-checks/1"), "hello (no pid) answered ok");
            Check(WaitFor(() => Active() && Id() == "fh2", 4000), "after hello: active, fh2");
            Thread.Sleep(1500);
            Check(Active(), "still active within the timeout");
            Check(WaitFor(() => !Active(), 7000), "no line for the timeout (4 s): no active game");

            Section("the mod's process exits: FH2 ends at once");
            using (var game = Idle("fake-game-host", 60))
            {
                Check(Send("hello fh2 1 bus-checks/1 pid=" + game.Id), "hello with the game's pid answered ok");
                Check(WaitFor(() => Active() && Id() == "fh2", 4000), "the game's process runs: active, fh2");
                game.Kill();
                game.WaitForExit();
            }
            // The last heartbeat may name the exited process: it does not count.
            var dead = Stopwatch.StartNew();
            bool ended = false;
            while (dead.ElapsedMilliseconds < 3500)
            {
                Send("alive fh2 pid=" + LastIdle);
                Thread.Sleep(500);
                ended = !Active();
                if (ended) break;
            }
            Check(ended, "the game's process exited: no active game within 3.5 s (before the 4 s timeout)");

            if (args.Length >= 3) RealPlugin(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            else Console.WriteLine("skip: the real FH2 plugin (pass plugin_test.exe and OpenBoxAchievements.dll)");
        }

        /// The FH2 plugin DLL, loaded by its own test host as the game loads it.
        static void RealPlugin(string host, string dll)
        {
            Exit();
            StartApp(8);
            Section("the real FH2 plugin: FH2 is active while the game runs with it");
            string plugins = Path.Combine(root, "plugin", "mods", "OpenBox Achievements", "plugins");
            Directory.CreateDirectory(plugins);
            File.Copy(dll, Path.Combine(plugins, "OpenBoxAchievements.dll"), true);
            File.WriteAllText(Path.Combine(plugins, "OpenBoxAchievements.ini"),
                "[Achievements]\r\nEnabled=1\r\nGame=fh2\r\nPauseButton=0\r\nSaves=" + Path.Combine(root, "plugin", "no-saves") + "\r\nRules=\r\n" +
                "[OpenBox]\r\nAppFolder=" + Path.Combine(root, "app") + "\r\nStartWithGame=0\r\nData=" + Path.Combine(root, "data") + "\r\nPipe=" + pipe + "\r\n" +
                "[Logging]\r\nEnabled=1\r\nTelemetry=0\r\n");
            const int seconds = 22;
            var game = Process.Start(new ProcessStartInfo(host, "bus \"" + Path.Combine(plugins, "OpenBoxAchievements.dll") + "\" " + pipe + " " + seconds)
                { UseShellExecute = false, RedirectStandardOutput = true });
            Check(WaitFor(() => Active() && Id() == "fh2", 8000), "the plugin connects: active, fh2");
            var clock = Stopwatch.StartNew();
            bool held = true;
            while (!game.HasExited && clock.ElapsedMilliseconds < (seconds - 6) * 1000)
            {
                Thread.Sleep(500);
                held &= Active() && Id() == "fh2";
            }
            Check(held && clock.ElapsedMilliseconds > 12000, "the plugin's heartbeats keep FH2 active past the app's 8 s timeout (" + clock.ElapsedMilliseconds / 1000 + " s)");
            game.WaitForExit(30000);
            Console.Write(game.StandardOutput.ReadToEnd());
            Check(game.ExitCode == 0, "the plugin test host ran cleanly");
            Check(WaitFor(() => !Active(), 4000), "the game process exited: no active game (no goodbye needed)");
            string log = File.Exists(Path.Combine(root, "data", "error.log")) ? File.ReadAllText(Path.Combine(root, "data", "error.log")) : "";
            Check(log.Contains("fh2: connected: 1 fh2-eo-openbox-achievements/") && log.Contains(" pid=" + game.Id),
                "the app logs the plugin's hello with its pid");
        }

        // ------------------------------------------------------------ helpers

        static Process app;

        static void StartApp(double busTimeout)
        {
            app = Process.Start(new ProcessStartInfo(exe, "--background --sandbox \"" + root + "\" --bus-timeout " + busTimeout)
                { UseShellExecute = false });
            Check(WaitFor(() => Send(""), 10000), "the app listens on its pipe (" + pipe + ")");
        }

        /// --exit: the running copy closes itself (and saves).
        static void Exit()
        {
            if (app == null) return;
            Process.Start(new ProcessStartInfo(exe, "--exit --sandbox \"" + root + "\"") { UseShellExecute = false }).WaitForExit(10000);
            if (!app.WaitForExit(10000)) { Fail("the app did not close on --exit"); app.Kill(); }
            app = null;
        }

        static int LastIdle;

        /// A copy of this EXE under another name, idling (a game's process).
        static Process Idle(string name, int seconds)
        {
            string folder = Path.Combine(root, "processes");
            Directory.CreateDirectory(folder);
            string copy = Path.Combine(folder, name + ".exe");
            if (!File.Exists(copy)) File.Copy(typeof(BusChecks).Assembly.Location, copy);
            var process = Process.Start(new ProcessStartInfo(copy, "--idle " + seconds) { UseShellExecute = false, CreateNoWindow = true });
            LastIdle = process.Id;
            return process;
        }

        /// One message to the app's pipe; true when it answered "ok".
        static bool Send(string lines)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut))
                {
                    client.Connect(1000);
                    byte[] bytes = Encoding.UTF8.GetBytes((lines.Length > 0 ? lines + "\n" : "") + "end\n");
                    client.Write(bytes, 0, bytes.Length);
                    client.Flush();
                    byte[] reply = new byte[8];
                    int n = client.Read(reply, 0, reply.Length);
                    return Encoding.ASCII.GetString(reply, 0, n) == "ok\n";
                }
            }
            catch (Exception error) when (error is IOException || error is TimeoutException) { return false; }
        }

        static Dictionary<string, object> Summary()
        {
            for (int i = 0; i < 20; ++i)
                try { return json.Deserialize<Dictionary<string, object>>(File.ReadAllText(summary)); }
                catch (Exception error) when (error is IOException || error is ArgumentException || error is UnauthorizedAccessException) { Thread.Sleep(25); }
            return new Dictionary<string, object>();
        }

        static bool Active() => Summary().TryGetValue("active", out var a) && a is bool b && b;
        static string Id() => Summary().TryGetValue("id", out var id) ? id as string : null;

        static double Played(string game)
        {
            string path = Path.Combine(root, "data", "games", game, "state.json");
            try { return File.Exists(path) ? Convert.ToDouble(json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path))["SecondsPlayed"]) : 0; }
            catch (Exception) { return 0; }
        }

        static bool WaitFor(Func<bool> condition, int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                if (condition()) return true;
                Thread.Sleep(100);
            }
            return condition();
        }

        static bool Always(Func<bool> condition, int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                if (!condition()) return false;
                Thread.Sleep(100);
            }
            return condition();
        }

        static void Section(string title) => Console.WriteLine(title);

        static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
            if (!ok) ++failures;
        }

        static void Fail(string what) => Check(false, what);
    }
}
