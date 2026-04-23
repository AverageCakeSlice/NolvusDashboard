using System.Diagnostics;
using Nolvus.Core.Events;
using Nolvus.Core.Services;
using Nolvus.Core.Utils;

namespace Nolvus.Services.Files.Extractor
{
    public class FileExtractor
    {
        private readonly ExtractProgress ExtractProgress;
        public event ExtractProgressChangedHandler ExtractProgressChanged;
        private string FileName;

        public FileExtractor()
        {
            ExtractProgress = new ExtractProgress();
        }

        private void TriggerProgressEvent(int Percent, string FileName)
        {
            if (ExtractProgressChanged != null)
            {
                ExtractProgress.ProgressPercentage = Percent;
                ExtractProgress.FileName = FileName;
                ExtractProgressChanged(this, ExtractProgress);
            }
        }

        public async Task ExtractFile(string File, string Output, ExtractProgressChangedHandler OnProgress)
        {
            ServiceSingleton.Logger.Log("File to extract: " + File);
            ServiceSingleton.Logger.Log("Outpath path: " + Output);

            FileName = Path.GetFileName(File);

            try
            {
                if (OnProgress != null)
                    ExtractProgressChanged += OnProgress;

                if (!Directory.Exists(Output))
                    Directory.CreateDirectory(Output);

                var sevenZipPath = Path.Combine(ServiceSingleton.Folders.LibDirectory, "7z");

                var psi = new ProcessStartInfo
                {
                    FileName = sevenZipPath,
                    Arguments = $"x -bsp1 -y \"{File}\" -o\"{Output}\" -mmt=off",
                    WorkingDirectory = ServiceSingleton.Folders.LibDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                // 1. Start draining stdout line-by-line for progress reporting.
                //    Must start BEFORE waiting so the pipe never fills and blocks 7z.
                var progressTask = Task.Run(async () =>
                {
                    string line;
                    while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                    {
                        if (line.Length >= 4 && line[3] == '%' && int.TryParse(line[..3], out var pct))
                            TriggerProgressEvent(pct, FileName);
                    }
                });

                // 2. Start draining stderr concurrently for the same reason.
                var stderrTask = proc.StandardError.ReadToEndAsync();

                // 3. Wait for the process to exit on a dedicated OS thread.
                //    WaitForExitAsync() hangs indefinitely on this system — PosixWait
                //    calls waitpid() directly which is the only reliable mechanism here.
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                int pid = proc.Id;
                new Thread(() =>
                {
                    try { tcs.TrySetResult(PosixWait.WaitForExitBlocking(pid)); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                }) { IsBackground = true, Name = $"7z-wait-{pid}" }.Start();

                int exitCode = await tcs.Task;

                // 4. Let the stream readers finish consuming any remaining buffered data.
                string errorOutput = await stderrTask;
                await progressTask;

                if (exitCode != 0)
                    throw new Exception($"Error during File extraction {FileName} (exit code {exitCode}): {errorOutput}");

                TriggerProgressEvent(100, FileName);
            }
            catch (Exception ex)
            {
                ServiceSingleton.Logger.Log(ex.Message);
                throw;
            }
            finally
            {
                if (OnProgress != null)
                {
                    try
                    {
                        ExtractProgressChanged -= OnProgress;
                    }
                    catch { }
                }
            }
        }
    }
}
