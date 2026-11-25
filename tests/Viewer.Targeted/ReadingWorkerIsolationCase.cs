using System.Runtime.InteropServices;
using LecteurWord.Platform;

static class ReadingWorkerIsolationCase
{
    internal static void Run(string directory)
    {
        const int Internet = 2;
        const int Unix = 1;
        const int Stream = 1;
        const int Datagram = 2;
        const int OperationNotPermitted = 1;
        string sentinel = Path.Combine(directory, "sentinel.txt");
        File.WriteAllText(sentinel, "Contenu synthétique réservé au test");
        Available(() => open(sentinel, 0), "lecture du témoin");
        Available(() => socket(Internet, Stream, 0), "socket TCP");
        Available(() => socket(Internet, Datagram, 0), "socket UDP");
        Available(() => socket(Unix, Stream, 0), "socket IPC local");
        using var ready = new ManualResetEventSlim();
        using var filtered = new ManualResetEventSlim();
        int threadResult = 0;
        var sibling = new Thread(() =>
        {
            ready.Set();
            if (filtered.Wait(TimeSpan.FromSeconds(2)))
            {
                int fd = open(sentinel, 0);
                threadResult = fd == -1 && Marshal.GetLastPInvokeError() == OperationNotPermitted ? 1 : -1;
                if (fd >= 0) close(fd);
            }
        }) { IsBackground = true };
        sibling.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Fil de contrôle indisponible.");
        Console.WriteLine("Lecture du témoin et création des trois familles de sockets possibles avant confinement.");
        if (!ReadingWorkerIsolation.Apply()) throw new InvalidOperationException("Filtre seccomp indisponible.");
        filtered.Set();
        Denied(() => open(sentinel, 0), "lecture de fichier");
        Denied(() => open(sentinel, 1), "écriture de fichier");
        Denied(() => socket(Internet, Stream, 0), "socket TCP");
        Denied(() => socket(Internet, Datagram, 0), "socket UDP");
        Denied(() => socket(Unix, Stream, 0), "socket IPC local");
        if (!sibling.Join(TimeSpan.FromSeconds(2)) || threadResult != 1)
            throw new InvalidOperationException("Le fil déjà démarré échappe au filtre.");
        Console.WriteLine("Lecture, écriture, TCP, UDP, IPC et accès depuis un fil déjà démarré refusés par le noyau.");

        static void Available(Func<int> check, string label)
        {
            int fd = check();
            if (fd < 0) throw new InvalidOperationException($"Témoin indisponible avant confinement ({label}).");
            close(fd);
        }

        static void Denied(Func<int> check, string label)
        {
            int fd = check();
            if (fd == -1 && Marshal.GetLastPInvokeError() == OperationNotPermitted) return;
            if (fd >= 0) close(fd);
            throw new InvalidOperationException($"Filtre absent ou incorrect ({label}).");
        }
    }

    [DllImport("libc", SetLastError = true)]
    static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int socket(int domain, int type, int protocol);

    [DllImport("libc")]
    static extern int close(int fd);
}
