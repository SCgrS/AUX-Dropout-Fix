// AUX Dropout Fix: Some portable speakers (such as the JBL Charge 4) treat a weak signal on their AUX input as
// silence and mute themselves. This tool sends a constant, inaudible low-frequency signal to the PC's AUX output so
// the speaker's detection circuit never sees silence and quiet passages no longer cut out at low PC volume.
//
// The signal level at the output is kept independent of the Windows volume: when Windows volume goes down, the
// signal's own amplitude goes up by the same amount (up to MaxDb). The signal is only sent while another program is
// playing sound on that output, and for HoldSeconds afterwards, so the speaker can still go idle on its own. While
// nothing plays, the audio stream is stopped: the PC can go to sleep and the program uses next to no CPU.
//
// On outputs with hardware volume, Windows mixes all programs at full scale and lowers the volume only after that.
// At low Windows volume the boosted signal plus loud sound from another program could go over full scale and
// crackle, so the signal steps back while the other sound is loud. The speaker does not mute then anyway.
//
// Built with the C# compiler that ships with Windows (C# 5, .NET Framework 4): Build.cmd.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace AuxDropoutFix
{
    static class Program
    {
        // Settings: can be changed with "name=value" lines in %LOCALAPPDATA%\AUXDropoutFix\settings.txt.
        // Empty Output means the first active speaker or line output on the "High Definition Audio Device" adapter.
        static string Output = "";
        static double Frequency = 20;
        static double TargetDb = -40;
        static double MaxDb = -3;
        static double HoldSeconds = 15;

        // The other programs' sound plus the signal is kept below this (full scale is 1).
        const double MixCeiling = 0.9;
        // A session counts as playing only above this peak (-80 dB), so an open but silent stream does not turn the
        // signal on.
        const double AudiblePeak = 0.0001;

        static readonly string DataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AUXDropoutFix");
        static EventWaitHandle exitEvent;
        static readonly int ownProcessId = Process.GetCurrentProcess().Id;

        [MTAThread]
        static int Main(string[] args)
        {
            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            bool silent = Array.Exists(args, a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase));
            try
            {
                exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\AUXDropoutFixExit");
            }
            catch (UnauthorizedAccessException)
            {
                // The running copy was started with "Run as administrator", so this copy cannot ask it to quit.
                Setup.ShowError("AUX Dropout Fix is running as administrator right now. Restart Windows, then try again.", silent);
                return 1;
            }
            if (command == "/exit")
            {
                exitEvent.Set();
                return 0;
            }
            if (command == "/uninstall") return Setup.Uninstall(exitEvent, silent);
            // Started from anywhere other than the install folder (for example the Downloads folder): install and quit.
            if (command != "/quiet" && !Setup.IsInstalledCopy()) return Setup.Install(exitEvent, silent);
            bool firstInstance;
            Mutex instanceLock;
            try
            {
                instanceLock = new Mutex(true, @"Local\AUXDropoutFixSingle", out firstInstance);
            }
            catch (UnauthorizedAccessException)
            {
                Setup.ShowError("AUX Dropout Fix is running as administrator right now. Restart Windows, then try again.", silent);
                return 1;
            }
            if (!firstInstance) return 0;
            exitEvent.Reset();

            ReadSettings();
            Log("Started. Output: " + (Output.Length > 0 ? Output : "(High Definition Audio Device)") + ", " + Frequency
                + " Hz, target " + TargetDb + " dB, max " + MaxDb + " dB, hold " + HoldSeconds + " s");

            bool notFoundLogged = false;
            string lastOutput = null, lastError = null;
            int errorWait = 3000;
            while (!exitEvent.WaitOne(0))
            {
                var attempt = Stopwatch.StartNew();
                try
                {
                    string name;
                    IMMDevice device = FindOutput(out name);
                    if (device == null)
                    {
                        if (!notFoundLogged) { Log("Output not found, waiting."); notFoundLogged = true; }
                        lastOutput = null;
                        exitEvent.WaitOne(5000);
                        continue;
                    }
                    notFoundLogged = false;
                    if (name != lastOutput) { Log("Output: " + name); lastOutput = name; }
                    Run(device);
                }
                catch (Exception e)
                {
                    // An error that keeps coming back right away (for example an unsupported audio format) is logged
                    // once, and each retry waits twice as long, up to a minute.
                    if (attempt.Elapsed.TotalMinutes > 1) { lastError = null; errorWait = 3000; }
                    if (e.Message != lastError) Log("Error: " + e.Message);
                    lastError = e.Message;
                    exitEvent.WaitOne(errorWait);
                    errorWait = Math.Min(errorWait * 2, 60000);
                }
            }
            Log("Exited.");
            GC.KeepAlive(instanceLock);
            return 0;
        }

        // Streams the signal to one output while other programs play sound on it. Returns when exit is requested,
        // throws if the output goes away.
        static void Run(IMMDevice device)
        {
            IAudioClient client = null;
            IAudioEndpointVolume volume = null;
            IAudioRenderClient renderer = null;
            IAudioSessionManager2 sessionManager = null;
            var meters = new List<IAudioMeterInformation>();
            var processes = new List<uint>();
            AutoResetEvent bufferEvent = null;
            IntPtr format = IntPtr.Zero;
            uint mmcssIndex = 0;
            IntPtr mmcss = IntPtr.Zero;
            bool started = false;
            try
            {
                Guid g = typeof(IAudioClient).GUID;
                object o;
                Check(device.Activate(ref g, ClsctxAll, IntPtr.Zero, out o), "IAudioClient");
                client = (IAudioClient)o;
                g = typeof(IAudioEndpointVolume).GUID;
                Check(device.Activate(ref g, ClsctxAll, IntPtr.Zero, out o), "IAudioEndpointVolume");
                volume = (IAudioEndpointVolume)o;
                g = typeof(IAudioSessionManager2).GUID;
                Check(device.Activate(ref g, ClsctxAll, IntPtr.Zero, out o), "IAudioSessionManager2");
                sessionManager = (IAudioSessionManager2)o;

                Check(client.GetMixFormat(out format), "GetMixFormat");
                int tag = (ushort)Marshal.ReadInt16(format, 0);
                int channels = Marshal.ReadInt16(format, 2);
                int rate = Marshal.ReadInt32(format, 4);
                int bits = Marshal.ReadInt16(format, 14);
                bool isFloat = tag == 3;
                if (tag == 0xFFFE)
                {
                    var sub = new byte[16];
                    Marshal.Copy(new IntPtr(format.ToInt64() + 24), sub, 0, 16);
                    isFloat = new Guid(sub) == new Guid("00000003-0000-0010-8000-00aa00389b71");
                }
                if (!(isFloat && bits == 32) && !(!isFloat && bits == 16))
                    throw new Exception("Unsupported audio format: tag " + tag + ", " + bits + " bit");

                // Event driven with a 20 ms buffer: Windows wakes us every 10 ms while the stream runs.
                Check(client.Initialize(0, AudclntStreamflagsEventcallback, 200000, 0, format, IntPtr.Zero), "Initialize");
                bufferEvent = new AutoResetEvent(false);
                Check(client.SetEventHandle(bufferEvent.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
                uint bufferFrames;
                Check(client.GetBufferSize(out bufferFrames), "GetBufferSize");
                g = typeof(IAudioRenderClient).GUID;
                Check(client.GetService(ref g, out o), "GetService");
                renderer = (IAudioRenderClient)o;
                OptOutOfDucking(client);

                try { mmcss = AvSetMmThreadCharacteristics("Audio", ref mmcssIndex); } catch { }

                var floatSamples = new float[bufferFrames * channels];
                var shortSamples = new short[bufferFrames * channels];
                var wakeUps = new WaitHandle[] { exitEvent, bufferEvent };
                double phase = 0, step = 2 * Math.PI * Frequency / rate, previous = 0, gate = 0, limit = MixCeiling;
                double ceiling = Math.Pow(10, MaxDb / 20.0);
                int fastRamp = Math.Max(1, rate / 200);
                long silentFrames = 0;
                var clock = Stopwatch.StartNew();
                double lastSoundAt = -1e9, nextScan = 0;

                Log("Ready: " + rate + " Hz, " + channels + " channels, " + bits + " bit, buffer " + bufferFrames + " frames");
                while (true)
                {
                    // While the stream is stopped nothing wakes us, so we look at the other programs twice a second.
                    if (started)
                    {
                        int woken = WaitHandle.WaitAny(wakeUps, 2000);
                        if (woken == 0) return;
                        if (woken == WaitHandle.WaitTimeout) throw new Exception("Audio device is not responding.");
                    }
                    else if (exitEvent.WaitOne(500)) return;

                    // While running, the sessions are looked at only after the buffer has been filled again (below), so
                    // that a slow look cannot make our own stream run dry.
                    double now = clock.Elapsed.TotalSeconds;
                    if (!started && now >= nextScan)
                    {
                        FindPlayingSessions(device, client, sessionManager, meters, processes);
                        nextScan = now + 0.5;
                    }

                    // The other programs' peaks over the last 10 ms, measured before the Windows volume.
                    double otherPeak = 0;
                    int loudest = -1;
                    float loudestPeak = 0;
                    for (int i = 0; i < meters.Count; i++)
                    {
                        float peak;
                        if (meters[i].GetPeakValue(out peak) < 0) continue;
                        otherPeak += peak;
                        if (peak > loudestPeak) { loudestPeak = peak; loudest = i; }
                    }
                    bool audible = otherPeak > AudiblePeak;
                    if (audible) lastSoundAt = now;
                    bool open = audible || now - lastSoundAt < HoldSeconds;

                    if (!started)
                    {
                        if (!open) continue;
                        Check(client.Start(), "Start");
                        started = true;
                        gate = 0;
                        previous = 0;
                        silentFrames = 0;
                        Log(loudest >= 0 ? "Sound playing (PID " + processes[loudest] + "), signal on." : "Signal on.");
                        continue;
                    }

                    uint padding;
                    Check(client.GetCurrentPadding(out padding), "GetCurrentPadding");
                    uint free = bufferFrames - padding;
                    if (free > 0)
                    {
                        double seconds = (double)free / rate;
                        gate = open ? Math.Min(1, gate + seconds / 0.5) : Math.Max(0, gate - seconds / 2.0);
                        // Steps back at once when the other sound gets loud, and comes back within 0.3 s when it gets quiet.
                        double room = Math.Max(0, MixCeiling - otherPeak);
                        limit = room < limit ? room : Math.Min(room, limit + seconds / 0.3);

                        float masterDb;
                        Check(volume.GetMasterVolumeLevel(out masterDb), "GetMasterVolumeLevel");
                        double target = gate * Math.Min(Math.Min(ceiling, limit), Math.Pow(10, (TargetDb - masterDb) / 20.0));

                        // Going down (Windows volume turned up, loud sound starting) takes 5 ms, going up spans the whole
                        // buffer. The older samples already in the buffer still play at their level, up to 20 ms.
                        int ramp = target < previous ? Math.Min(fastRamp, (int)free) : (int)free;
                        IntPtr data;
                        Check(renderer.GetBuffer(free, out data), "GetBuffer");
                        for (int i = 0; i < free; i++)
                        {
                            double amplitude = i < ramp ? previous + (target - previous) * (i + 1) / ramp : target;
                            double s = amplitude * Math.Sin(phase);
                            phase += step;
                            if (phase >= 2 * Math.PI) phase -= 2 * Math.PI;
                            for (int c = 0; c < channels; c++)
                            {
                                if (isFloat) floatSamples[i * channels + c] = (float)s;
                                else shortSamples[i * channels + c] = (short)(s * 32767);
                            }
                        }
                        silentFrames = target == 0 && previous == 0 ? silentFrames + free : 0;
                        previous = target;
                        if (isFloat) Marshal.Copy(floatSamples, 0, data, (int)free * channels);
                        else Marshal.Copy(shortSamples, 0, data, (int)free * channels);
                        Check(renderer.ReleaseBuffer(free, 0), "ReleaseBuffer");
                    }

                    // Nothing has played for HoldSeconds and the whole buffer holds silence: stop the stream.
                    if (!open && silentFrames >= bufferFrames)
                    {
                        Check(client.Stop(), "Stop");
                        Check(client.Reset(), "Reset");
                        started = false;
                        Log("No sound playing, signal off.");
                    }
                    else if (now >= nextScan)
                    {
                        FindPlayingSessions(device, client, sessionManager, meters, processes);
                        nextScan = now + 0.5;
                    }
                }
            }
            finally
            {
                if (started) { try { client.Stop(); } catch { } }
                if (mmcss != IntPtr.Zero) { try { AvRevertMmThreadCharacteristics(mmcss); } catch { } }
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                foreach (var meter in meters) Release(meter);
                Release(sessionManager); Release(renderer); Release(volume); Release(client); Release(device);
                if (bufferEvent != null) bufferEvent.Dispose();
            }
        }

        // Collects the peak meters of the sessions that other programs have active on the output right now. First makes
        // sure that the output is still there and our stream still works: while the stream is stopped, nothing else
        // would notice, and the signal would come on late once sound starts.
        static void FindPlayingSessions(IMMDevice device, IAudioClient client, IAudioSessionManager2 manager,
            List<IAudioMeterInformation> meters, List<uint> processes)
        {
            int deviceState;
            uint padding;
            if (device.GetState(out deviceState) < 0 || deviceState != DeviceStateActive)
                throw new Exception("The output is no longer available.");
            Check(client.GetCurrentPadding(out padding), "GetCurrentPadding");
            foreach (var meter in meters) Release(meter);
            meters.Clear();
            processes.Clear();
            IAudioSessionEnumerator list;
            Check(manager.GetSessionEnumerator(out list), "GetSessionEnumerator");
            try
            {
                int count;
                Check(list.GetCount(out count), "GetCount");
                for (int i = 0; i < count; i++)
                {
                    object o;
                    if (list.GetSession(i, out o) < 0 || o == null) continue;
                    var session = o as IAudioSessionControl2;
                    var meter = o as IAudioMeterInformation;
                    int state;
                    uint process;
                    if (session != null && meter != null && session.GetState(out state) >= 0 && state == 1
                        && session.GetProcessId(out process) >= 0 && process != ownProcessId)
                    {
                        meters.Add(meter);
                        processes.Add(process);
                    }
                    else Release(o);
                }
            }
            finally { Release(list); }
        }

        // Windows lowers other programs' sound during calls (the Communications tab of the classic Sound settings),
        // which would weaken the signal too. Our stream opts out of that.
        static void OptOutOfDucking(IAudioClient client)
        {
            Guid g = new Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"); // IAudioSessionControl
            object o;
            if (client.GetService(ref g, out o) < 0) return;
            var session = o as IAudioSessionControl2;
            if (session != null) session.SetDuckingPreference(true);
            Release(o);
        }

        // Finds the active output whose name is Output (for example "Speakers (High Definition Audio Device)"). With no
        // Output set, takes the first active speaker or line output on the "High Definition Audio Device" adapter.
        static IMMDevice FindOutput(out string name)
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
            try
            {
                IMMDeviceCollection list;
                Check(enumerator.EnumAudioEndpoints(0, DeviceStateActive, out list), "EnumAudioEndpoints");
                try
                {
                    uint count;
                    Check(list.GetCount(out count), "GetCount");
                    for (uint i = 0; i < count; i++)
                    {
                        IMMDevice device;
                        if (list.Item(i, out device) < 0) continue;
                        string id;
                        if (device.GetId(out id) >= 0 && Matches(id, out name)) return device;
                        Release(device);
                    }
                    name = null;
                    return null;
                }
                finally { Release(list); }
            }
            finally { Release(enumerator); }
        }

        static bool Matches(string id, out string name)
        {
            string description, adapter;
            int formFactor;
            ReadDeviceName(id, out description, out adapter, out formFactor);
            name = description + " (" + adapter + ")";
            if (Output.Length > 0)
                return Output.Equals(name, StringComparison.OrdinalIgnoreCase);
            // Form factor 1 is speakers, 2 is line out. Headphones (3), headsets (5) and HDMI or DisplayPort outputs (9)
            // are left out. Unlike the output's name, this does not depend on the Windows language.
            return "High Definition Audio Device".Equals(adapter, StringComparison.OrdinalIgnoreCase)
                && (formFactor == 1 || formFactor == 2);
        }

        // Reads an output's name from the Windows registry: description ("Speakers"), adapter name and form factor.
        // The 64-bit view is opened explicitly: this is a 32-bit process, which would otherwise find nothing there.
        static void ReadDeviceName(string id, out string description, out string adapter, out int formFactor)
        {
            description = ""; adapter = ""; formFactor = -1;
            int dot = id.LastIndexOf("}.", StringComparison.Ordinal);
            if (dot < 0) return;
            string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + id.Substring(dot + 2) + @"\Properties";
            using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = hive.OpenSubKey(key))
            {
                if (k == null) return;
                description = (k.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string) ?? "";
                adapter = (k.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string) ?? "";
                object value = k.GetValue("{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},0");
                if (value is int) formFactor = (int)value;
            }
        }

        static void ReadSettings()
        {
            string path = Path.Combine(DataFolder, "settings.txt");
            try
            {
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#")) continue;
                    string name = eq < 0 ? "" : line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = eq < 0 ? "" : line.Substring(eq + 1).Trim();
                    double number;
                    bool isNumber = double.TryParse(value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out number);
                    // The limits keep a typo from making the signal audible or loud. They also rule out NaN and infinity.
                    if (name == "output") Output = value;
                    else if (name == "frequency" && isNumber && number >= 10 && number <= 30) Frequency = number;
                    else if (name == "targetdb" && isNumber && number >= -60 && number <= -30) TargetDb = number;
                    else if (name == "maxdb" && isNumber && number >= -60 && number <= -3) MaxDb = number;
                    else if (name == "holdseconds" && isNumber && number >= 0 && number <= 3600) HoldSeconds = number;
                    else Log("Setting ignored: " + line.Trim());
                }
            }
            catch (Exception e)
            {
                Log("Settings could not be read: " + e.Message);
            }
        }

        static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(DataFolder);
                string path = Path.Combine(DataFolder, "log.txt");
                // Past 200 KB the log becomes log.old.txt, so the lines before the switch are not lost at once.
                if (File.Exists(path) && new FileInfo(path).Length > 200000)
                {
                    string old = Path.Combine(DataFolder, "log.old.txt");
                    File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        static void Check(int hr, string what)
        {
            if (hr < 0) throw new Exception(what + " failed (0x" + hr.ToString("X8") + ")");
        }

        static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) { try { Marshal.ReleaseComObject(o); } catch { } }
        }

        const int ClsctxAll = 23;
        const int DeviceStateActive = 1;
        const uint AudclntStreamflagsEventcallback = 0x00040000;

        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr AvSetMmThreadCharacteristics(string task, ref uint index);
        [DllImport("avrt.dll")]
        static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator list);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }
}
