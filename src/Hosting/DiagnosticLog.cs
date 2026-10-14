using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace SinmaiAlpha.Hosting;

// Disk writes never run on the gameplay thread. Bound both queue and session file.
internal sealed class DiagnosticLog : IDisposable
{
    private readonly BlockingCollection<string> queue = new(256);
    private readonly Thread worker;
    private readonly StreamWriter output;
    private int dropped;
    internal DiagnosticLog(string path)
    {
        output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
        worker = new Thread(Drain) { IsBackground = true, Name = "Sinmai diagnostics" };
        worker.Start();
    }
    internal void Enqueue(string line)
    {
        try { if (!queue.TryAdd(line)) Interlocked.Increment(ref dropped); }
        catch (InvalidOperationException) { }
    }
    private void Drain()
    {
        try
        {
            long bytes = 0;
            foreach (var line in queue.GetConsumingEnumerable())
            {
                var lost = Interlocked.Exchange(ref dropped, 0);
                if (lost > 0) output.WriteLine("DIAGNOSTIC QUEUE DROPPED=" + lost);
                bytes += Encoding.UTF8.GetByteCount(line) + 2;
                if (bytes > 32 * 1024 * 1024) { output.WriteLine("SESSION SIZE LIMIT 32 MiB; recording stopped."); break; }
                output.WriteLine(line); output.Flush();
            }
        }
        catch { /* Full or unavailable disk must not affect gameplay. */ }
        finally { try { queue.CompleteAdding(); output.Dispose(); } catch { } }
    }
    public void Dispose()
    {
        queue.CompleteAdding();
        if (Thread.CurrentThread != worker) worker.Join(1000);
    }
}
