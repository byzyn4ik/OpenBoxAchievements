using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpenBoxAchievements
{
    /// Which games are being played, as their achievement mods tell it over
    /// the pipe (EventInbox, docs\EVENTS.md). A game is connected while its
    /// mod keeps talking: any line that names it (hello, unlocked, progress,
    /// alive) counts; a mod that has nothing to send says "alive <game>"
    /// every few seconds. It ends with "bye <game>", when the mod's process
    /// (pid=<n> on its lines) exits, or after Timeout without a line.
    static class Bus
    {
        /// No line from a game's mod for this long: it is no longer played.
        public static TimeSpan Timeout = TimeSpan.FromSeconds(15);
        public static Func<DateTime> Clock = () => DateTime.UtcNow;

        sealed class Client
        {
            public DateTime Seen;
            public int Process; // 0: not told
        }

        static readonly Dictionary<string, Client> clients = new Dictionary<string, Client>(StringComparer.OrdinalIgnoreCase);

        /// A line from the game's mod; process: its pid, or 0. True when the
        /// game was not connected before.
        public static bool Seen(string game, int process)
        {
            lock (clients)
            {
                bool was = ConnectedLocked(game);
                if (!clients.TryGetValue(game, out var client)) clients[game] = client = new Client();
                client.Seen = Clock();
                if (process > 0) client.Process = process;
                else if (!was) client.Process = 0;
                return !was;
            }
        }

        /// "bye <game>": the mod is done.
        public static void Gone(string game)
        {
            lock (clients) clients.Remove(game);
        }

        public static bool Connected(string game)
        {
            lock (clients) return ConnectedLocked(game);
        }

        static bool ConnectedLocked(string game)
        {
            if (game == null || !clients.TryGetValue(game, out var client)) return false;
            if (Clock() - client.Seen > Timeout || (client.Process > 0 && !Alive(client.Process)))
            {
                clients.Remove(game);
                return false;
            }
            return true;
        }

        /// "pid=<n>" among a line's words: the mod's process.
        public static int ProcessOf(string[] words)
        {
            foreach (var word in words)
                if (word.StartsWith("pid=", StringComparison.Ordinal) &&
                    int.TryParse(word.Substring(4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid) && pid > 0)
                    return pid;
            return 0;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        /// The process is still running (one we may not open, e.g. elevated, counts as running).
        static bool Alive(int pid)
        {
            const uint Synchronize = 0x00100000, WaitTimeout = 0x102;
            IntPtr handle = OpenProcess(Synchronize, false, pid);
            if (handle == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5; // access denied: it exists
            try { return WaitForSingleObject(handle, 0) == WaitTimeout; }
            finally { CloseHandle(handle); }
        }
    }
}
