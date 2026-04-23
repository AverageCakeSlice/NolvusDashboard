using System;
using System.Runtime.InteropServices;

namespace Nolvus.Core.Utils
{
    /// <summary>
    /// Direct waitpid() via P/Invoke for Linux process reaping.
    ///
    /// WaitForExitAsync() and WaitForExit() do not work reliably on this system —
    /// they hang indefinitely, leaving child processes zombified.
    /// Calling waitpid() directly is the only mechanism proven to reap children here.
    ///
    /// Always call from a dedicated OS thread (not a thread-pool thread) so the
    /// thread pool remains free to service async I/O callbacks.
    /// </summary>
    public static class PosixWait
    {
        [DllImport("libc", SetLastError = true)]
        private static extern int waitpid(int pid, out int status, int options);

        public static int WaitForExitBlocking(int pid)
        {
            while (true)
            {
                int rc = waitpid(pid, out int status, 0);

                if (rc == pid)
                    return DecodeExitCode(status);

                if (rc == -1)
                {
                    int err = Marshal.GetLastWin32Error();
                    const int ECHILD = 10; // already reaped by another thread
                    if (err == ECHILD) return 0;
                    throw new Exception($"waitpid({pid}) failed errno={err}");
                }
            }
        }

        private static int DecodeExitCode(int status)
        {
            if ((status & 0x7F) == 0)
                return (status >> 8) & 0xFF;   // normal exit
            return 128 + (status & 0x7F);       // killed by signal
        }
    }
}
