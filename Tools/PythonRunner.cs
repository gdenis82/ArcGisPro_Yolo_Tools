using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Win32;

namespace ArcGisProAppYolo.Tools
{
    public static class PythonRunner
    {
        /// <summary>
        /// Запускает Python-скрипт с аргументами. Если pythonExe==null используется 'python' из PATH или propy.bat если найден.
        /// Возвращает код возврата процесса.
        /// </summary>
        public static Task<int> RunPythonScriptAsync(string pythonExe, string scriptPath, string arguments, Action<string> onStdout = null, Action<string> onStderr = null, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<int>();
            CancellationTokenRegistration ctr = default;
            Process proc = null;

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetResult(-2);
                    return tcs.Task;
                }

                // If pythonExe is null, try to locate propy.bat prioritizing LocalAppData, then registry/PATH/ProgramFiles
                string runner = pythonExe;
                bool useCmd = false;

                if (string.IsNullOrEmpty(runner))
                {
                    var propy = FindPropyPrioritizingLocalAppData();
                    if (!string.IsNullOrEmpty(propy))
                    {
                        // propy is a .bat - we'll execute it via cmd.exe /c "propy" args
                        runner = propy;
                        useCmd = true;
                        Logger.Log($"Detected propy for running scripts: {propy}");
                    }
                    else
                    {
                        runner = "python"; // fallback to python in PATH
                        Logger.Log("propy not found, falling back to 'python' from PATH");
                    }
                }

                ProcessStartInfo psi;

                var enc = GetSystemOemEncoding() ?? Encoding.Default;

                if (useCmd)
                {
                    // wrap call in cmd.exe so .bat runs correctly
                    // Use nested quoting so cmd.exe invokes the full propy path with arguments
                    var cmdArgs = $"/c \"\"{runner}\" \"{scriptPath}\" {arguments}\"";
                    psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = cmdArgs,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = enc,
                        StandardErrorEncoding = enc,
                        WorkingDirectory = Path.GetDirectoryName(runner) ?? Environment.CurrentDirectory
                    };
                }
                else
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = runner,
                        Arguments = $"\"{scriptPath}\" {arguments}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = enc,
                        StandardErrorEncoding = enc
                    };
                }

                proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

                proc.OutputDataReceived += (s, e) => { if (e.Data != null) onStdout?.Invoke(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) onStderr?.Invoke(e.Data); };
                proc.Exited += (s, e) =>
                {
                    try
                    {
                        ctr.Dispose();
                    }
                    catch { }
                    tcs.TrySetResult(proc.ExitCode);
                };
                Logger.Log($"Starting process: {psi.FileName} {psi.Arguments} (WorkingDirectory={psi.WorkingDirectory})");

                try
                {
                    var started = proc.Start();
                    if (!started)
                    {
                        var msg = "Process did not start (Process.Start returned false).";
                        onStderr?.Invoke(msg);
                        Logger.Log(msg);
                        tcs.TrySetResult(-1);
                        return tcs.Task;
                    }
                }
                catch (Exception startEx)
                {
                    var msg = $"Failed to start process: {startEx.Message}";
                    onStderr?.Invoke(msg);
                    try { File.AppendAllText(Path.Combine(Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory, "process_start_error.txt"), msg + Environment.NewLine + startEx); } catch { }
                    Logger.Log(msg);
                    tcs.TrySetResult(-1);
                    return tcs.Task;
                }

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (cancellationToken.CanBeCanceled)
                {
                    ctr = cancellationToken.Register(() =>
                    {
                        try
                        {
                            if (proc == null || proc.HasExited)
                                return;

                            Logger.Log($"INFO: Cancelling python process tree (PID={proc.Id})");
                            proc.Kill(entireProcessTree: true);
                            tcs.TrySetResult(-2);
                        }
                        catch (Exception killEx)
                        {
                            Logger.Log($"ERROR: Failed to cancel python process: {killEx}");
                            tcs.TrySetResult(-2);
                        }
                    });
                }

                return tcs.Task;
            }
            catch (Exception ex)
            {
                try
                {
                    ctr.Dispose();
                }
                catch { }
                onStderr?.Invoke(ex.Message);
                tcs.TrySetResult(-1);
                return tcs.Task;
            }
        }

        private static Encoding GetSystemOemEncoding()
        {
            try
            {
                var cp = GetOEMCP();
                if (cp != 0)
                {
                    return Encoding.GetEncoding((int)cp);
                }
            }
            catch { }
            try { return Encoding.Default; } catch { return Encoding.UTF8; }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        private static string FindPropyPrioritizingLocalAppData()
        {
            try
            {
                // 1) LocalAppData candidate (priority)
                var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var localCandidate = Path.Combine(localApp, "Programs", "ArcGIS", "Pro", "bin", "Python", "Scripts", "propy.bat");
                if (File.Exists(localCandidate)) return localCandidate;

                // 2) Registry uninstall entries
                var root = DetectArcGISProInstallRoot();
                if (!string.IsNullOrEmpty(root))
                {
                    var p = Path.Combine(root, "bin", "Python", "Scripts", "propy.bat");
                    if (File.Exists(p)) return p;
                }

                // 3) PATH
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim(), "propy.bat");
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }

                // 4) ProgramFiles candidates
                var pfCandidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ArcGIS", "Pro", "bin", "Python", "Scripts", "propy.bat"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ArcGIS", "Pro", "bin", "Python", "Scripts", "propy.bat")
                };
                foreach (var c in pfCandidates) if (File.Exists(c)) return c;
            }
            catch (Exception ex)
            {
                Logger.Log($"FindPropy error: {ex.Message}");
            }

            return null;
        }

        private static string DetectArcGISProInstallRoot()
        {
            try
            {
                foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                        if (uninstall == null) continue;
                        foreach (var sub in uninstall.GetSubKeyNames())
                        {
                            using var sk = uninstall.OpenSubKey(sub);
                            if (sk == null) continue;
                            var displayName = (sk.GetValue("DisplayName") as string) ?? string.Empty;
                            if (!displayName.Contains("ArcGIS Pro", StringComparison.OrdinalIgnoreCase)) continue;
                            var installLoc = (sk.GetValue("InstallLocation") as string) ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(installLoc) && Directory.Exists(installLoc))
                                return installLoc;

                            var disp = (sk.GetValue("DisplayIcon") as string) ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(disp))
                            {
                                var exePath = disp.Trim().Trim('"').Split(',')[0];
                                if (File.Exists(exePath))
                                {
                                    var dir = Path.GetDirectoryName(exePath);
                                    if (!string.IsNullOrWhiteSpace(dir))
                                    {
                                        var root = Path.GetFullPath(Path.Combine(dir, ".."));
                                        if (Directory.Exists(root)) return root;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                // fallback candidates
                var candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ArcGIS", "Pro"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ArcGIS", "Pro"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ArcGIS", "Pro")
                };

                foreach (var c in candidates)
                    if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c))
                        return c;
            }
            catch { }

            return null;
        }
    }
}
