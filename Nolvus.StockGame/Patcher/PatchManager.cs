using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;
using Nolvus.Core.Events;
using Nolvus.Core.Services;
using Nolvus.Core.Utils;
using Nolvus.Core.Enums;
using Nolvus.StockGame.Core;
using Nolvus.StockGame.Meta;

namespace Nolvus.StockGame.Patcher
{
    public class PatcherManager
    {
        #region Fields

        string _WorkingDir = string.Empty;
        string _LibDir = string.Empty;
        string _PatchDir = string.Empty;

        #endregion

        #region Events

        public event DownloadProgressChangedHandler OnDownload;
        public event ExtractProgressChangedHandler OnExtract;

        event OnItemProcessedHandler OnItemProcessedEvent;

        public event OnItemProcessedHandler OnItemProcessed
        {
            add
            {
                if (OnItemProcessedEvent != null)
                {
                    lock (OnItemProcessedEvent)
                    {
                        OnItemProcessedEvent += value;
                    }
                }
                else
                {
                    OnItemProcessedEvent = value;
                }
            }
            remove
            {
                if (OnItemProcessedEvent != null)
                {
                    lock (OnItemProcessedEvent)
                    {
                        OnItemProcessedEvent -= value;
                    }
                }
            }
        }

        event OnStepProcessedHandler OnStepProcessedEvent;

        public event OnStepProcessedHandler OnStepProcessed
        {
            add
            {
                if (OnStepProcessedEvent != null)
                {
                    lock (OnStepProcessedEvent)
                    {
                        OnStepProcessedEvent += value;
                    }
                }
                else
                {
                    OnStepProcessedEvent = value;
                }
            }
            remove
            {
                if (OnStepProcessedEvent != null)
                {
                    lock (OnStepProcessedEvent)
                    {
                        OnStepProcessedEvent -= value;
                    }
                }
            }
        }

        #endregion

        public PatcherManager(string WorkingDir, string LibDir, string PatchDir)
        {
            _WorkingDir = WorkingDir;
            _LibDir = LibDir;
            _PatchDir = PatchDir;
        }

        #region Methods

        private void Downloading(object sender, DownloadProgress e)
        {
            if (OnDownload != null)
            {
                OnDownload(this, e);
            }
        }

        private void Extracting(object sender, ExtractProgress e)
        {
            if (OnExtract != null)
            {
                OnExtract(this, e);
            }
        }

        private void ElementProcessed(int Value, int Total, StockGameProcessStep Step, string ItemName)
        {
            OnItemProcessedHandler Handler = this.OnItemProcessedEvent;
            ItemProcessedEventArgs Event = new ItemProcessedEventArgs(Value, Total, Step, ItemName);
            if (Handler != null) Handler(this, Event);
        }

        private void StepProcessed(string Step)
        {
            OnStepProcessedHandler Handler = this.OnStepProcessedEvent;
            StepProcessedEventArgs Event = new StepProcessedEventArgs(0, 0, Step);
            if (Handler != null) Handler(this, Event);
        }

        private async Task DoDownloadPatchFile(PatchingInstruction Instruction)
        {            
            var Tsk = Task.Run(async () => 
            {
                try
                {
                    if (!File.Exists(Path.Combine(_PatchDir, Instruction.PatchFile)))
                    {
                        this.StepProcessed("Downloading patch file " + Instruction.PatchFile);

                        string DownloadedFile = Path.Combine(_PatchDir, Instruction.PatchFile);

                        await ServiceSingleton.Files.DownloadFile(Instruction.DownLoadLink, DownloadedFile, Downloading);

                        this.StepProcessed("Patching file downloaded");
                    }
                }
                catch(Exception ex)
                {
                    ServiceSingleton.Logger.Log(string.Format("Error during patch file download with message {0}", ex.Message));
                    throw ex;
                }              
            });

            await Tsk;                              
        }    


        private async Task DoPatchFile(PatchingInstruction Instruction, string SourceDir, string DestDir, bool KeepPatches)
        {
            try
            {
                await DoDownloadPatchFile(Instruction);

                StepProcessed("Patching game file : " + Instruction.DestFile.Name);
                ElementProcessed(0, 1, StockGameProcessStep.PatchGameFile, Instruction.DestFile.Name);

                string SourceFileName = Instruction.SourceFile.GetFullName(SourceDir);
                string DestinationFileName = Instruction.DestFile.GetFullName(DestDir);
                string PatchFileName = Path.Combine(_PatchDir, Instruction.PatchFile);

                Directory.CreateDirectory(DestDir);

                ServiceSingleton.Logger.Log("Executing xdelta3 patch:");
                ServiceSingleton.Logger.Log($"  Source      = {SourceFileName}");
                ServiceSingleton.Logger.Log($"  Patch       = {PatchFileName}");
                ServiceSingleton.Logger.Log($"  Destination = {DestinationFileName}");

                var xdeltaPath = Path.Combine(ServiceSingleton.Folders.LibDirectory, "xdelta3");
                var psi = new ProcessStartInfo
                {
                    WorkingDirectory = DestDir,
                    FileName = xdeltaPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("-s");
                psi.ArgumentList.Add(SourceFileName);     // arg 1
                psi.ArgumentList.Add(PatchFileName);      // arg 2
                psi.ArgumentList.Add(DestinationFileName); // arg 3

                using var process = new Process();
                process.StartInfo = psi;

                process.Start();

                // 1. Drain both pipes concurrently before waiting — prevents buffer deadlock.
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                // 2. Reap via waitpid() on a dedicated thread — WaitForExitAsync hangs on this system.
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                int pid = process.Id;
                new Thread(() =>
                {
                    try { tcs.TrySetResult(PosixWait.WaitForExitBlocking(pid)); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                }) { IsBackground = true, Name = $"xdelta3-dopatch-wait-{pid}" }.Start();

                int exitCode = await tcs.Task;

                // 3. Collect remaining output after process has exited.
                string stdout = await stdoutTask;
                string stderr = await stderrTask;

                ServiceSingleton.Logger.Log($"Exit code: {exitCode}");
                if (!string.IsNullOrWhiteSpace(stdout))
                    ServiceSingleton.Logger.Log($"stdout: {stdout}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    ServiceSingleton.Logger.Log($"stderr: {stderr}");

                if (exitCode != 0)
                {
                    throw new GameFilePatchingException(
                        $"Failed to patch game file : {Instruction.DestFile.Name}",
                        stderr + Environment.NewLine + stdout
                    );
                }

                if (!KeepPatches && File.Exists(PatchFileName))
                    File.Delete(PatchFileName);

                StepProcessed("Game file : " + Instruction.DestFile.Name + " patched");
                ElementProcessed(1, 1, StockGameProcessStep.PatchGameFile, Instruction.DestFile.Name);
            }
            catch (Exception ex)
            {
                ServiceSingleton.Logger.Log($"Error during game file patching with message: {ex.Message}");
                throw;
            }
        }  

        private void CheckPatchedFile(PatchingInstruction Instruction, string DestDir)
        {
            string FileName = Instruction.DestFile.GetFullName(DestDir);

            StepProcessed("Checking integrity for patched game file " + Instruction.DestFile.Name);
            ElementProcessed(0, 1, StockGameProcessStep.CheckPatchedGameFile, Instruction.DestFile.Name);            

            string FileHash = ServiceSingleton.Files.GetHash(FileName);

            if (FileHash != Instruction.DestFile.Hash)
            {
                throw new GameFileIntegrityException("Hash for game file : " + FileName + " does not match!");
            }

            this.StepProcessed("Patched game file " + Instruction.DestFile.Name + " integrity ok");
            ElementProcessed(1, 1, StockGameProcessStep.CheckPatchedGameFile, Instruction.DestFile.Name);
        }

        public async Task PatchFile(string sourceFile, string destinationFile, string patchFile)
        {
            try
            {
                var workingDirectory = new FileInfo(destinationFile).DirectoryName ?? ".";
                var xdeltaPath = Path.Combine(ServiceSingleton.Folders.LibDirectory, "xdelta3");

                var psi = new ProcessStartInfo
                {
                    FileName = xdeltaPath,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("-s");
                psi.ArgumentList.Add(sourceFile);
                psi.ArgumentList.Add(patchFile);
                psi.ArgumentList.Add(destinationFile);

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                // 1. Drain both pipes concurrently before waiting — prevents buffer deadlock.
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                ServiceSingleton.Logger.Log("Patching await start");

                // 2. Reap via waitpid() on a dedicated thread — WaitForExitAsync hangs on this system.
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                int pid = proc.Id;
                new Thread(() =>
                {
                    try { tcs.TrySetResult(PosixWait.WaitForExitBlocking(pid)); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                }) { IsBackground = true, Name = $"xdelta3-wait-{pid}" }.Start();

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
                cts.Token.Register(() => tcs.TrySetCanceled());

                int exitCode;
                try
                {
                    exitCode = await tcs.Task;
                }
                catch (TaskCanceledException)
                {
                    // Kill() is wrapped in try/catch — the process may have exited between the
                    // timeout firing and Kill() being called (race condition).
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw new Exception($"xdelta3 timed out after 5 minutes patching {Path.GetFileName(destinationFile)}");
                }

                ServiceSingleton.Logger.Log("Patching await end");

                // 3. Collect remaining output after process has exited.
                string stderr = await stderrTask;
                await stdoutTask;

                if (exitCode != 0)
                    throw new Exception($"xdelta3 failed (exit {exitCode})\n{stderr}");

                ServiceSingleton.Logger.Log($"Patched {Path.GetFileName(destinationFile)} successfully (exit {exitCode})");
            }
            catch (Exception ex)
            {
                ServiceSingleton.Logger.Log($"PatchFile error: {ex.Message}");
                throw;
            }
        }

        private void DeleteFile(PatchingInstruction Instruction, string DestDir)
        {
            switch (Instruction.SourceFile.Location)
            {
                case FileLocation.Data:
                    File.Delete(Path.Combine(DestDir, "Data", Instruction.SourceFile.Name));
                    break;

                default:
                    File.Delete(Path.Combine(DestDir, Instruction.SourceFile.Name));
                    break;
            }            
        }

        public async Task PatchFile(PatchingInstruction Instruction, string SourceDir, string DestDir, bool KeepPatches)
        {            
            var Tsk = Task.Run(async ()=>
            {
                try
                {
                    //await DoDownloadBinaries();

                    StepProcessed("About to patch game file : " + Instruction.DestFile.Name);

                    switch (Instruction.Action)
                    {
                        case PatcherAction.Delete:
                            DeleteFile(Instruction, DestDir);
                            StepProcessed("Game file : " + Instruction.DestFile.Name + " deleted");
                            break;
                        case PatcherAction.Patch:                            
                            await DoPatchFile(Instruction, SourceDir, DestDir, KeepPatches);                                                        
                            CheckPatchedFile(Instruction, DestDir);                            
                            break;
                    }                    
                }
                catch(Exception ex)
                {
                    throw ex;
                }
            });

            await Tsk;            
        }

        #endregion

    }
}
