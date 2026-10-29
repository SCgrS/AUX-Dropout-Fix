// Installing and uninstalling for the current user, without administrator rights.
// Install: copies the exe to %LOCALAPPDATA%\Programs\AUXDropoutFix, adds it to Windows startup and to
// Settings › Apps (so it can be uninstalled from there), then starts the installed copy.
// Uninstall: stops the running copy, removes both registry entries and deletes the program and data folders.
//
// Windows Forms (the message boxes) is only used on these paths. The background copy never loads it, which saves
// several MB of memory, so Program.Main must not call into Windows Forms directly.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AuxDropoutFix
{
    static class Setup
    {
        const string AppName = "AUX Dropout Fix";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AUXDropoutFix";

        static readonly string InstallFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\AUXDropoutFix");
        static readonly string InstalledExe = Path.Combine(InstallFolder, "AUXDropoutFix.exe");
        static readonly string DataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AUXDropoutFix");

        // The entry assembly's path rather than Application.ExecutablePath, which loads Windows Forms and turns "%41"
        // in a folder name into "A".
        static string OwnExe { get { return Assembly.GetEntryAssembly().Location; } }

        public static bool IsInstalledCopy()
        {
            return string.Equals(Path.GetFullPath(OwnExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);
        }

        public static int Install(EventWaitHandle exitEvent, bool silent)
        {
            string newExe = InstalledExe + ".new";
            bool stopped = false;
            try
            {
                Directory.CreateDirectory(InstallFolder);
                // Writing the bytes instead of copying the file leaves out the "downloaded from the internet" mark, so
                // Windows does not warn about the installed copy. The old copy keeps running until this has worked.
                File.WriteAllBytes(newExe, File.ReadAllBytes(OwnExe));
                stopped = StopRunningCopy(exitEvent);
                ReplaceInstalledExe(newExe);

                using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
                    run.SetValue(AppName, "\"" + InstalledExe + "\"");
                using (var u = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    u.SetValue("DisplayName", AppName);
                    u.SetValue("DisplayVersion", Application.ProductVersion);
                    u.SetValue("DisplayIcon", InstalledExe);
                    u.SetValue("InstallLocation", InstallFolder);
                    u.SetValue("UninstallString", "\"" + InstalledExe + "\" /uninstall");
                    u.SetValue("QuietUninstallString", "\"" + InstalledExe + "\" /uninstall /silent");
                    u.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    u.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    u.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024) + 1, RegistryValueKind.DWord);
                }
                StartInstalledCopy();
                if (!silent)
                    MessageBox.Show(AppName + " is installed and running.\n\n"
                        + "It starts with Windows and works in the background, there is no window. "
                        + "To remove it, go to Settings › Apps › Installed apps.", AppName,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception e)
            {
                if (File.Exists(InstalledExe)) { try { File.Delete(newExe); } catch { } }
                // The running copy was stopped for the update: start it again, so the speaker keeps working.
                if (stopped && File.Exists(InstalledExe)) { try { StartInstalledCopy(); } catch { } }
                ShowError("Installation failed: " + e.Message, silent);
                return 1;
            }
        }

        public static int Uninstall(EventWaitHandle exitEvent, bool silent)
        {
            if (!silent && MessageBox.Show("Remove " + AppName + " from this computer?", AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return 1;
            try
            {
                StopRunningCopy(exitEvent);
                using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (run != null) run.DeleteValue(AppName, false);
                // The settings and the log are not worth failing the uninstall over.
                try { if (Directory.Exists(DataFolder)) Directory.Delete(DataFolder, true); } catch { }
                // Last, so that the app is still listed in Settings › Apps if anything above fails.
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
                if (!silent) MessageBox.Show(AppName + " has been removed.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                // This exe may be running from the install folder and cannot be deleted while it runs. So only now,
                // after the message box is closed, a short-lived cmd is started that deletes the folder after we quit.
                if (Directory.Exists(InstallFolder)) DeleteAfterExit(InstallFolder);
                return 0;
            }
            catch (Exception e)
            {
                ShowError("Uninstall failed: " + e.Message, silent);
                return 1;
            }
        }

        // Kept out of line so that Program.Main can call it without loading Windows Forms on every start.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ShowError(string message, bool silent)
        {
            if (!silent) MessageBox.Show(message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // Asks the running copy to quit and waits up to 8 seconds for it. Returns whether a copy was running.
        // It waits for the running copy's single-instance mutex, not for processes by name: those would also include
        // another installer showing its message box, or a copy running for another Windows user.
        static bool StopRunningCopy(EventWaitHandle exitEvent)
        {
            Mutex running;
            if (!Mutex.TryOpenExisting(@"Local\AUXDropoutFixSingle", out running)) return false;
            using (running)
            {
                exitEvent.Set();
                try
                {
                    try
                    {
                        if (!running.WaitOne(8000)) throw new Exception("The running copy did not stop.");
                    }
                    catch (AbandonedMutexException) { }
                    running.ReleaseMutex();
                }
                finally { exitEvent.Reset(); }
            }
            return true;
        }

        // The old copy has only just quit, so its exe can stay locked for a moment. A locked exe can still be renamed,
        // so it is moved aside first and put back if the new one cannot take its place.
        static void ReplaceInstalledExe(string newExe)
        {
            string oldExe = InstalledExe + ".old";
            if (File.Exists(oldExe)) File.Delete(oldExe);
            bool hadOld = File.Exists(InstalledExe);
            if (hadOld) File.Move(InstalledExe, oldExe);
            try
            {
                File.Move(newExe, InstalledExe);
            }
            catch
            {
                if (hadOld) File.Move(oldExe, InstalledExe);
                throw;
            }
            // If it is still locked after 3 seconds, the next install or the uninstall removes it.
            for (int attempt = 1; attempt <= 30 && File.Exists(oldExe); attempt++)
            {
                try { File.Delete(oldExe); }
                catch { Thread.Sleep(100); }
            }
        }

        static void StartInstalledCopy()
        {
            // Through the shell, so that the background copy does not inherit the installer's handles: a script that
            // reads the installer's output would otherwise wait for as long as the background copy runs. The install
            // folder as working folder, so that it does not keep the installer's folder (for example an unzipped
            // download) locked either.
            var info = new ProcessStartInfo(InstalledExe);
            info.WorkingDirectory = InstallFolder;
            info.UseShellExecute = true;
            using (Process.Start(info)) { }
        }

        static void DeleteAfterExit(string folder)
        {
            // Full paths and the system folder as working folder: no other cmd.exe or PING.EXE can be picked up, and
            // cmd does not sit in the folder it deletes. It tries once a second for 15 seconds, until this exe has quit.
            string system = Environment.SystemDirectory;
            var cmd = new ProcessStartInfo(Path.Combine(system, "cmd.exe"),
                "/c for /l %i in (1,1,15) do (" + Path.Combine(system, "PING.EXE") + " -n 2 127.0.0.1 >nul & rmdir /s /q \""
                + folder + "\" 2>nul & if not exist \"" + folder + "\" exit)");
            cmd.WorkingDirectory = system;
            cmd.CreateNoWindow = true;
            cmd.UseShellExecute = false;
            using (Process.Start(cmd)) { }
        }
    }
}
