using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenBoxAchievements
{
    /// The local pipe \\.\pipe\OpenBoxAchievements.Events.v1. A game's
    /// achievement mod (FH2: fh2-achievements.dll, which owns the rules) sends
    /// its results, one per line, closed by "end"; OpenBox answers "ok" once
    /// they are applied (see docs\EVENTS.md):
    ///   hello <game> 1 <producer>
    ///   unlocked <game> <id> [<time>]
    ///   progress <game> <id> <current>/<target>
    ///   end
    sealed class EventInbox : IDisposable
    {
        // Tests use their own name so that they never reach a running OpenBox.
        public static string PipeName = "OpenBoxAchievements.Events.v1";
        const int MaxMessage = 256 * 1024;
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly Func<string[], bool> messages;
        readonly Task listener;
        NamedPipeServerStream pipe;

        public EventInbox(Func<string[], bool> messages)
        {
            this.messages = messages;
            listener = Task.Run(Listen);
        }

        async Task Listen()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using (var connection = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                        1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 1024))
                    {
                        pipe = connection;
                        await connection.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                        string text = await ReadMessage(connection).ConfigureAwait(false);
                        if (text == null) continue;
                        bool applied = messages(text.Split('\n'));
                        if (!applied) continue;
                        byte[] ok = Encoding.ASCII.GetBytes("ok\n");
                        await connection.WriteAsync(ok, 0, ok.Length, stop.Token).ConfigureAwait(false);
                        await connection.FlushAsync(stop.Token).ConfigureAwait(false);
                        try { connection.WaitForPipeDrain(); } catch (IOException) { }
                    }
                }
                catch (Exception error) when (error is IOException || error is OperationCanceledException || error is ObjectDisposedException)
                {
                    if (stop.IsCancellationRequested) break;
                }
                catch (Exception error)
                {
                    // Any other failure must not end the listener: the game would
                    // see OpenBox running but have no pipe to send unlocks to.
                    if (stop.IsCancellationRequested) break;
                    Log.Write("event pipe: " + error);
                    await Task.Delay(1000).ConfigureAwait(false);
                }
                finally { pipe = null; }
            }
        }

        /// Reads until the client closes its end, an "end" line, or a second
        /// without data. Null when nothing came.
        async Task<string> ReadMessage(NamedPipeServerStream connection)
        {
            var bytes = new MemoryStream();
            byte[] buffer = new byte[4096];
            while (bytes.Length < MaxMessage)
            {
                var read = connection.ReadAsync(buffer, 0, buffer.Length, stop.Token);
                if (await Task.WhenAny(read, Task.Delay(1000, stop.Token)).ConfigureAwait(false) != read) break;
                int count = await read.ConfigureAwait(false);
                if (count <= 0) break;
                bytes.Write(buffer, 0, count);
                string sofar = Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
                if (sofar.EndsWith("\nend\n") || sofar == "end\n" || sofar.EndsWith("\nend\r\n")) break;
            }
            return bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
        }

        public void Dispose()
        {
            stop.Cancel();
            pipe?.Dispose();
        }
    }
}
