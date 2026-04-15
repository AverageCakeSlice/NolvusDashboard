using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using Nolvus.Core.Services;
using Nolvus.Core.Utils;

namespace Nolvus.Package.Mods
{
    public class BsaUnPacking
    {
        public string FileName { get; set; }
        public string DirectoryName { get; set; }

        private FileInfo? GetBsaToUnpack(string extractDir)
        {
            var files = ServiceSingleton.Files.GetFiles(extractDir);

            if (string.IsNullOrWhiteSpace(DirectoryName))
            {
                return files.FirstOrDefault(x =>
                    x.Name.Equals(FileName, StringComparison.OrdinalIgnoreCase));
            }

            var normalizedDir = DirectoryName.Replace('\\', '/');

            return files.FirstOrDefault(f =>
            {
                if (!f.Name.Equals(FileName, StringComparison.OrdinalIgnoreCase))
                    return false;

                var dir = f.Directory.FullName.Replace('\\', '/');
                return dir.Contains(normalizedDir, StringComparison.OrdinalIgnoreCase);
            });
        }

        public async Task UnPack(string extractDir)
        {
            var bsaFile = GetBsaToUnpack(extractDir);
            var bsArchPath = Path.Combine(ServiceSingleton.Folders.LibDirectory, "BSArch");

            if (!File.Exists(bsArchPath))
                throw new FileNotFoundException($"BSArch not found: {bsArchPath}", bsArchPath);

            if (bsaFile == null)
                throw new Exception("Failed to unpack file : " + FileName + "==> File not found");

            var psi = new ProcessStartInfo
            {
                FileName = bsArchPath,
                WorkingDirectory = ServiceSingleton.Folders.LibDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add("unpack");
            psi.ArgumentList.Add(bsaFile.FullName);
            psi.ArgumentList.Add(bsaFile.DirectoryName);

            ServiceSingleton.Logger.Log($"Unpacking command line : \"{bsArchPath}\" unpack \"{bsaFile.FullName}\" \"{bsaFile.DirectoryName}\"");

            using var unpack = new Process { StartInfo = psi };
            unpack.Start();

            // 1. Drain both pipes concurrently before waiting for exit.
            //    Sequential reads deadlock if the process fills one pipe while we block on the other.
            var stdoutTask = unpack.StandardOutput.ReadToEndAsync();
            var stderrTask = unpack.StandardError.ReadToEndAsync();

            // 2. Wait for process exit via direct waitpid() — WaitForExitAsync hangs on this system.
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            int pid = unpack.Id;
            new Thread(() =>
            {
                try { tcs.TrySetResult(PosixWait.WaitForExitBlocking(pid)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }) { IsBackground = true, Name = $"bsarch-wait-{pid}" }.Start();

            int exitCode = await tcs.Task;

            // 3. Collect any remaining buffered output after the process has exited.
            string output = await stdoutTask + await stderrTask;

            if (exitCode == 0)
            {
                File.Delete(bsaFile.FullName);
            }
            else
            {
                throw new Exception("Failed to unpack file : " + FileName + "==>" + output);
            }
        }
    }
}
