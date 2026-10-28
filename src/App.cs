using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace OpenBoxAchievements
{
    static class Json
    {
        /// JavaScriptSerializer gives arrays as object[] or ArrayList.
        public static object[] List(object value) =>
            value as object[] ?? (value is ArrayList list ? list.ToArray() : new object[0]);
    }

    /// error.log in app data.
    static class Log
    {
        public static void Write(string text)
        {
            try { File.AppendAllText(Path.Combine(Paths.Data, "error.log"), DateTime.Now.ToString("s") + "  " + text + Environment.NewLine); }
            catch (Exception) { }
        }
    }

    /// Dark title bars (Windows 10 2004+ / 11) for the app's ordinary windows.
    static class Dark
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        public static void Apply(Window window) => window.SourceInitialized += (s, e) =>
        {
            int on = 1;
            DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(window).Handle, 20, ref on, sizeof(int));
        };
    }

    /// OpenBox Achievements: runs in the tray while games are played, reads
    /// each game's events, unlocks achievements by its manifest's rules, shows the
    /// toast and counts time played. Games are the folders under games\ beside
    /// the app. A second start (or the in-game button, through
    /// Local\OpenBoxAchievements.Show) brings up the page.
    /// --background starts in the tray only; --game <id> opens (or, with
    /// --background, defaults to) that game; --import [--game <id>] opens the
    /// wizard to load that game from Xbox (or to add a game); --test-toast
    /// shows a sample toast.
    sealed class App : Application
    {
        List<Game> games = new List<Game>();
        Settings settings;
        Game last;
        Viewer viewer;
        EventInbox eventInbox;
        Forms.NotifyIcon tray;
        DateTime lastTick = DateTime.UtcNow, lastSave = DateTime.UtcNow;
        static EventWaitHandle showSignal;
        // The game a start asked for (--game <id>), handed to the running copy.
        static System.IO.MemoryMappedFiles.MemoryMappedFile request;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr window);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr process);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AttachThreadInput(uint thread, uint to, bool attach);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        /// A running copy shown from the game or Game Bar is not the foreground
        /// app, so Windows would leave the page behind: share the foreground
        /// window's input for the moment of the switch (an Alt press, which
        /// also grants it, if that is not enough).
        static void Foreground(IntPtr window)
        {
            if (window == IntPtr.Zero || GetForegroundWindow() == window) return;
            uint current = GetCurrentThreadId(), front = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            bool attached = front != 0 && front != current && AttachThreadInput(current, front, true);
            BringWindowToTop(window);
            bool done = SetForegroundWindow(window);
            if (attached) AttachThreadInput(current, front, false);
            if (!done || GetForegroundWindow() != window)
            {
                keybd_event(0x12, 0, 0, UIntPtr.Zero); // Alt down
                keybd_event(0x12, 0, 2, UIntPtr.Zero); // Alt up
                SetForegroundWindow(window);
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            args = FromLink(args);
            var mutex = new Mutex(true, @"Local\OpenBoxAchievements", out bool created);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\OpenBoxAchievements.Show");
            request = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen(@"Local\OpenBoxAchievements.Game", 512);
            if (!created)
            {
                // The running copy may take the foreground: this start was the user's.
                if (!args.Contains("--background") || args.Contains("--import"))
                {
                    WriteRequest((args.Contains("--import") ? ImportPrefix : "") + RequestedGame(args));
                    AllowSetForegroundWindow(-1);
                    showSignal.Set();
                }
                return 0;
            }
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += (s, e) => app.Start(args);
            app.DispatcherUnhandledException += (s, e) => { Log.Write(e.Exception.ToString()); e.Handled = true; };
            app.Exit += (s, e) => { app.eventInbox?.Dispose(); foreach (var game in app.games) game.SaveState(); };
            int code = app.Run();
            GC.KeepAlive(mutex);
            return code;
        }

        void Start(string[] args)
        {
            Paths.MoveOldFiles();
            RegisterLink();
            settings = Settings.Load(Paths.Data);
            Lang.Apply(settings.Language);
            Toast.Sound = settings.ToastSound;
            Game.UseAchievementArt = settings.AchievementArtBackground;
            games = Game.LoadAll();
            eventInbox = new EventInbox(lines =>
            {
                // Applied on the UI thread before the game is answered "ok".
                bool applied = false;
                try { Dispatcher.Invoke(new Action(() => applied = OnGameMessages(lines)), TimeSpan.FromSeconds(3)); }
                catch (Exception error) when (error is OperationCanceledException || error is TaskCanceledException || error is InvalidOperationException) { }
                return applied;
            });
            last = Find(RequestedGame(args)) ?? games.FirstOrDefault();

            tray = new Forms.NotifyIcon
            {
                Text = "OpenBox Achievements",
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location),
                Visible = true,
            };
            TrayMenu();
            tray.DoubleClick += (s, e) => ShowViewer(null);

            var waiter = new Thread(() => { while (true) { showSignal.WaitOne(); string id = TakeRequest(); Dispatcher.BeginInvoke(new Action(() => OnRequest(id))); } }) { IsBackground = true };
            waiter.Start();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (s, e) => Tick();
            timer.Start();
            Tick();

            if (args.Contains("--test-toast") && games.Count > 0)
            {
                var sample = games[0].Catalog.Achievements.FirstOrDefault() ?? new Achievement { Id = "0", Name = Lang.T("Sample achievement", "Приклад досягнення"), Gamerscore = 10 };
                Toast.Show(sample, games[0].Art(sample));
            }
            if (args.Contains("--import")) Import(RequestedGame(args));
            else if (!args.Contains("--background")) ShowViewer(null);
        }

        /// A second start's request: a game id, or "import:<id>" (--import).
        const string ImportPrefix = "import:";

        void OnRequest(string id)
        {
            if (id != null && id.StartsWith(ImportPrefix, StringComparison.Ordinal))
            {
                string game = id.Substring(ImportPrefix.Length);
                Import(game.Length > 0 ? game : null);
            }
            else ShowViewer(Find(id) ?? Running());
        }

        /// --import: a known game (its manifest has the Xbox title ID) opens
        /// the wizard on loading it from Xbox; otherwise the Add game wizard.
        void Import(string id)
        {
            var game = Find(id);
            Log.Write("import requested (" + (id ?? "no game") + (game == null ? ", not found" : "") + ")");
            var wizard = OpenWizard(game);
            Foreground(new System.Windows.Interop.WindowInteropHelper(wizard).Handle);
        }

        /// The tray icon's menu, in the current language.
        void TrayMenu()
        {
            var menu = new Forms.ContextMenu();
            menu.MenuItems.Add(Lang.T("Open achievements", "Відкрити досягнення"), (s, e) => ShowViewer(null));
            menu.MenuItems.Add(Lang.T("Settings…", "Параметри…"), (s, e) => OpenSettings());
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(Lang.T("Exit", "Вийти"), (s, e) => { tray.Visible = false; Shutdown(); });
            var old = tray.ContextMenu;
            tray.ContextMenu = menu;
            old?.Dispose();
        }

        Game Running() =>games.FirstOrDefault(g => g.IsRunning);

        Game Find(string id) => id == null ? null : games.FirstOrDefault(g => string.Equals(g.Info.Id, id, StringComparison.OrdinalIgnoreCase));

        /// The link the Game Bar widget opens, openbox-achievements:show?game=<id>,
        /// becomes --game <id>.
        public const string LinkScheme = "openbox-achievements";

        static string[] FromLink(string[] args)
        {
            if (args.Length != 1 || !args[0].StartsWith(LinkScheme + ":", StringComparison.OrdinalIgnoreCase)) return args;
            var match = System.Text.RegularExpressions.Regex.Match(args[0], @"[?&]game=([A-Za-z0-9_\-]+)");
            return match.Success ? new[] { "--game", match.Groups[1].Value } : new string[0];
        }

        /// Registers the link for this user (HKCU, no admin), pointing at this EXE.
        static void RegisterLink()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + LinkScheme))
                {
                    key.SetValue("", "URL:OpenBox Achievements");
                    key.SetValue("URL Protocol", "");
                    using (var command = key.CreateSubKey(@"shell\open\command"))
                        command.SetValue("", "\"" + exe + "\" \"%1\"");
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }

        /// --game <id>: the game's folder name under games, e.g. fh2.
        static string RequestedGame(string[] args)
        {
            int i = Array.IndexOf(args, "--game");
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        static void WriteRequest(string id)
        {
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(id == null ? "" : id.Length > 200 ? id.Substring(0, 200) : id);
            using (var view = request.CreateViewAccessor())
            {
                view.WriteArray(4, bytes, 0, bytes.Length);
                view.Write(0, bytes.Length);
            }
        }

        static string TakeRequest()
        {
            using (var view = request.CreateViewAccessor())
            {
                int length = view.ReadInt32(0);
                if (length <= 0 || length > 400) return null;
                var bytes = new byte[length];
                view.ReadArray(4, bytes, 0, length);
                view.Write(0, 0);
                return System.Text.Encoding.Unicode.GetString(bytes);
            }
        }

        /// The Add game wizard, above the page when it is open.
        void AddGame() => OpenWizard(null);

        /// The wizard's restore mode: texts and art for a game that has only ids.
        void LoadData(Game game) => OpenWizard(game);

        Wizard OpenWizard(Game restore)
        {
            var wizard = new Wizard(settings, Reload, restore) { Topmost = viewer != null };
            wizard.Show();
            wizard.Activate();
            return wizard;
        }

        void OpenSettings()
        {
            var window = new SettingsWindow(settings, games, AddGame, SettingsSaved, RemoveGame) { Topmost = viewer != null };
            window.Show();
            window.Activate();
        }

        /// A game's folders (manifest; data, art and progress) to the Recycle
        /// Bin, where they can be restored from.
        void RemoveGame(Game game)
        {
            foreach (var folder in new[] { game.Folder, game.Local })
                try
                {
                    if (Directory.Exists(folder))
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(folder,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                catch (Exception error) { Log.Write("remove " + folder + ": " + error.Message); }
            games.Remove(game);
            if (last == game) last = null;
            bool open = viewer != null;
            Reload(null, open);
        }

        void Reload(string id) => Reload(id, true);

        /// Applies saved settings; an open page is shown again with them.
        void SettingsSaved()
        {
            Toast.Sound = settings.ToastSound;
            Game.UseAchievementArt = settings.AchievementArtBackground;
            bool ukrainian = Lang.Ukrainian;
            Lang.Apply(settings.Language);
            if (Lang.Ukrainian != ukrainian)
            {
                TrayMenu();
                var playing = Running();
                Widget.Write(playing ?? last ?? games.FirstOrDefault(), playing != null);
            }
            if (viewer != null) Reload(viewer.Game?.Info.Id ?? last?.Info.Id);
        }

        void Reload(string id, bool show)
        {
            foreach (var game in games) game.SaveState();
            games = Game.LoadAll();
            var added = games.FirstOrDefault(g => g.Info.Id == id);
            // The last game played, as reloaded (the old object has the old data).
            last = games.FirstOrDefault(g => g.Info.Id == last?.Info.Id) ?? games.FirstOrDefault();
            viewer?.Close();
            if (show && games.Count > 0) ShowViewer(added);
        }

        void Tick()
        {
            var now = DateTime.UtcNow;
            double elapsed = Math.Min(10, (now - lastTick).TotalSeconds);
            lastTick = now;
            bool changed = false, progressed = false, save = (now - lastSave).TotalSeconds >= 30;
            foreach (var game in games)
            {
                if (game.IsRunning)
                {
                    game.State.SecondsPlayed += elapsed;
                    last = game;
                    if (save) game.SaveState();
                }
                List<Achievement> unlocked;
                try { unlocked = game.Results.Poll(); }
                catch (IOException) { continue; }
                if (settings.Toasts)
                    for (int i = 0; i < unlocked.Count; ++i)
                        Toast.Show(unlocked[i], game.Art(unlocked[i]), game.Completed && i == unlocked.Count - 1);
                changed |= unlocked.Count > 0;
                progressed |= game.Results.Changed;
            }
            if (save) lastSave = now;
            if (changed) viewer?.Refresh();
            else if (progressed) viewer?.Stale();
            // The Game Bar widget: on any change, when a game starts or stops, and each minute.
            var playing = Running();
            if (changed || progressed || (playing != null) != widgetPlaying || ++widgetTicks % 40 == 1)
                Widget.Write(playing ?? last ?? games.FirstOrDefault(), playing != null);
            widgetPlaying = playing != null;
        }

        int widgetTicks;
        bool widgetPlaying;

        /// Results from a game's achievement mod (GameMessages):
        /// recorded, then toasts, page and widget. True: answer "ok".
        bool OnGameMessages(string[] lines)
        {
            var changed = new HashSet<Game>();
            var toasts = new List<(Game, Achievement)>();
            bool understood = GameMessages.Apply(games, lines, changed, toasts);
            if (settings.Toasts)
                for (int i = 0; i < toasts.Count; ++i)
                {
                    var (game, achievement) = toasts[i];
                    Toast.Show(achievement, game.Art(achievement), game.Completed && !toasts.Skip(i + 1).Any(t => t.Item1 == game));
                }
            if (toasts.Count > 0) viewer?.Refresh();
            else if (changed.Count > 0) viewer?.Stale();
            if (changed.Count > 0)
            {
                var playing = Running();
                Widget.Write(playing ?? last ?? games.FirstOrDefault(), playing != null);
            }
            return understood;
        }

        void ShowViewer(Game game)
        {
            game = game ?? last;
            Log.Write("show requested (" + (game?.Name ?? "no game") + ", " + games.Count + " game(s))");
            Tick(); // read the latest events first, so the page is current
            if (viewer == null)
            {
                viewer = new Viewer(games, game, OpenSettings, LoadData);
                viewer.Closed += (s, e) => viewer = null;
                viewer.Show();
            }
            else
            {
                viewer.Select(game);
                viewer.Refresh();
            }
            // Full screen above everything, the running game included; B or Esc closes.
            viewer.WindowState = WindowState.Maximized;
            viewer.Topmost = true;
            viewer.Activate();
            Foreground(new System.Windows.Interop.WindowInteropHelper(viewer).Handle);
        }
    }
}
