// Installing and uninstalling for the current user, without administrator rights.
// Install: copies the exe to %LOCALAPPDATA%\Programs\AUXDropoutFix, adds it to Windows startup and to
// Settings › Apps (so it can be uninstalled from there), then starts the installed copy.
// Uninstall: stops the running copy, removes both registry entries and deletes the program and data folders.
using System;
using System.Diagnostics;
using System.IO;
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

        public static bool IsInstalledCopy()
        {
            return string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(InstalledExe),
                StringComparison.OrdinalIgnoreCase);
        }

        public static int Install(EventWaitHandle exitEvent, bool silent)
        {
            try
            {
                StopRunningCopy(exitEvent);
                Directory.CreateDirectory(InstallFolder);
                File.Copy(Application.ExecutablePath, InstalledExe, true);

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
                Process.Start(InstalledExe);
                if (!silent)
                    MessageBox.Show(AppName + " is installed and running.\n\n"
                        + "It starts with Windows and works in the background, there is no window. "
                        + "To remove it, go to Settings › Apps › Installed apps.", AppName,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception e)
            {
                if (!silent) MessageBox.Show("Installation failed: " + e.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        public static int Uninstall(EventWaitHandle exitEvent)
        {
            bool silent = Environment.GetCommandLineArgs().Length > 2
                && Environment.GetCommandLineArgs()[2].Equals("/silent", StringComparison.OrdinalIgnoreCase);
            if (!silent && MessageBox.Show("Remove " + AppName + " from this computer?", AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return 1;
            try
            {
                StopRunningCopy(exitEvent);
                using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (run != null) run.DeleteValue(AppName, false);
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
                if (Directory.Exists(DataFolder)) Directory.Delete(DataFolder, true);
                if (Directory.Exists(InstallFolder))
                {
                    // This exe may be running from the install folder, so a short-lived cmd deletes it after we quit.
                    var cmd = new ProcessStartInfo("cmd.exe",
                        "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallFolder + "\"");
                    cmd.CreateNoWindow = true;
                    cmd.UseShellExecute = false;
                    Process.Start(cmd);
                }
                if (!silent) MessageBox.Show(AppName + " has been removed.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception e)
            {
                if (!silent) MessageBox.Show("Uninstall failed: " + e.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // Asks the running copy to quit and waits up to 8 seconds for it.
        static void StopRunningCopy(EventWaitHandle exitEvent)
        {
            int own = Process.GetCurrentProcess().Id;
            var others = Array.FindAll(Process.GetProcessesByName("AUXDropoutFix"), p => p.Id != own);
            if (others.Length == 0) return;
            exitEvent.Set();
            foreach (var p in others)
                if (!p.WaitForExit(8000)) throw new Exception("The running copy did not stop.");
            exitEvent.Reset();
        }
    }
}
