// AUX Dropout Fix: Some portable speakers (such as the JBL Charge 4) treat a weak signal on their AUX input as
// silence and mute themselves. This tool sends a constant, inaudible low-frequency signal to the PC's AUX output so
// the speaker's detection circuit never sees silence and quiet passages no longer cut out at low PC volume.
//
// The signal level at the output is kept independent of the Windows volume: when Windows volume goes down, the
// signal's own amplitude goes up by the same amount (up to MaxDb). The signal is only sent while another program is
// playing sound on that output, and for HoldSeconds afterwards, so the speaker can still go idle on its own.
//
// Built with the C# compiler that ships with Windows (C# 5, .NET Framework 4): Build.cmd.
using System;
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
        // Empty Output means the first active output on the "High Definition Audio Device" adapter.
        static string Output = "";
        static double Frequency = 20;
        static double TargetDb = -40;
        static double MaxDb = -3;
        static double HoldSeconds = 15;

        static readonly string DataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AUXDropoutFix");
        static EventWaitHandle exitEvent;
        static volatile bool otherSoundPlaying;
        static readonly int ownProcessId = Process.GetCurrentProcess().Id;

        [MTAThread]
        static int Main(string[] args)
        {
            exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\AUXDropoutFixExit");
            if (args.Length > 0 && args[0].Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                exitEvent.Set();
                return 0;
            }
            bool firstInstance;
            var instanceLock = new Mutex(true, @"Local\AUXDropoutFixSingle", out firstInstance);
            if (!firstInstance) return 0;
            exitEvent.Reset();

            ReadSettings();
            Log("Started. Output: " + (Output.Length > 0 ? Output : "(High Definition Audio Device)") + ", " + Frequency
                + " Hz, target " + TargetDb + " dB, max " + MaxDb + " dB, hold " + HoldSeconds + " s");

            var watcher = new Thread(WatchSessions);
            watcher.IsBackground = true;
            watcher.Start();

            bool notFoundLogged = false;
            while (!exitEvent.WaitOne(0))
            {
                try
                {
                    IMMDevice device = FindOutput();
                    if (device == null)
                    {
                        if (!notFoundLogged) { Log("Output not found, waiting."); notFoundLogged = true; }
                        exitEvent.WaitOne(5000);
                        continue;
                    }
                    notFoundLogged = false;
                    Play(device);
                }
                catch (Exception e)
                {
                    Log("Error: " + e.Message);
                    exitEvent.WaitOne(3000);
                }
            }
            Log("Exited.");
            GC.KeepAlive(instanceLock);
            return 0;
        }

        // Streams the signal to one output. Returns when exit is requested, throws if the output goes away.
        static void Play(IMMDevice device)
        {
            IAudioClient client = null;
            IAudioEndpointVolume volume = null;
            IAudioRenderClient renderer = null;
            IntPtr format = IntPtr.Zero;
            uint mmcssIndex = 0;
            IntPtr mmcss = IntPtr.Zero;
            try
            {
                Guid g = typeof(IAudioClient).GUID;
                object o;
                Check(device.Activate(ref g, ClsctxAll, IntPtr.Zero, out o), "IAudioClient");
                client = (IAudioClient)o;
                g = typeof(IAudioEndpointVolume).GUID;
                Check(device.Activate(ref g, ClsctxAll, IntPtr.Zero, out o), "IAudioEndpointVolume");
                volume = (IAudioEndpointVolume)o;

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

                // Event driven with a 20 ms buffer: Windows wakes us every 10 ms. If the Windows volume jumps up, at most
                // this much of the older, stronger signal is still played.
                Check(client.Initialize(0, AudclntStreamflagsEventcallback, 200000, 0, format, IntPtr.Zero), "Initialize");
                var bufferEvent = new AutoResetEvent(false);
                Check(client.SetEventHandle(bufferEvent.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
                uint bufferFrames;
                Check(client.GetBufferSize(out bufferFrames), "GetBufferSize");
                g = typeof(IAudioRenderClient).GUID;
                Check(client.GetService(ref g, out o), "GetService");
                renderer = (IAudioRenderClient)o;

                try { mmcss = AvSetMmThreadCharacteristics("Audio", ref mmcssIndex); } catch { }

                var floatSamples = new float[bufferFrames * channels];
                var shortSamples = new short[bufferFrames * channels];
                double phase = 0, step = 2 * Math.PI * Frequency / rate, previous = 0, gate = 0;
                double ceiling = Math.Pow(10, MaxDb / 20.0);
                var clock = Stopwatch.StartNew();
                double lastSoundAt = -1e9;

                Check(client.Start(), "Start");
                Log("Stream started: " + rate + " Hz, " + channels + " channels, " + bits + " bit, buffer " + bufferFrames + " frames");
                while (true)
                {
                    if (exitEvent.WaitOne(0)) return;
                    if (!bufferEvent.WaitOne(2000)) throw new Exception("Audio device is not responding.");
                    uint padding;
                    Check(client.GetCurrentPadding(out padding), "GetCurrentPadding");
                    uint free = bufferFrames - padding;
                    if (free == 0) continue;

                    double now = clock.Elapsed.TotalSeconds;
                    if (otherSoundPlaying) lastSoundAt = now;
                    bool open = now - lastSoundAt < HoldSeconds;
                    double seconds = (double)free / rate;
                    gate = open ? Math.Min(1, gate + seconds / 0.5) : Math.Max(0, gate - seconds / 2.0);

                    float masterDb;
                    Check(volume.GetMasterVolumeLevel(out masterDb), "GetMasterVolumeLevel");
                    double target = gate * Math.Min(ceiling, Math.Pow(10, (TargetDb - masterDb) / 20.0));

                    IntPtr data;
                    Check(renderer.GetBuffer(free, out data), "GetBuffer");
                    for (int i = 0; i < free; i++)
                    {
                        double s = (previous + (target - previous) * (i + 1) / free) * Math.Sin(phase);
                        phase += step;
                        if (phase >= 2 * Math.PI) phase -= 2 * Math.PI;
                        for (int c = 0; c < channels; c++)
                        {
                            if (isFloat) floatSamples[i * channels + c] = (float)s;
                            else shortSamples[i * channels + c] = (short)(s * 32767);
                        }
                    }
                    previous = target;
                    if (isFloat) Marshal.Copy(floatSamples, 0, data, (int)free * channels);
                    else Marshal.Copy(shortSamples, 0, data, (int)free * channels);
                    Check(renderer.ReleaseBuffer(free, 0), "ReleaseBuffer");
                }
            }
            finally
            {
                if (client != null) { try { client.Stop(); } catch { } }
                if (mmcss != IntPtr.Zero) { try { AvRevertMmThreadCharacteristics(mmcss); } catch { } }
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                Release(renderer); Release(volume); Release(client); Release(device);
            }
        }

        // Every half second, checks whether a session from another process is active on the output.
        static void WatchSessions()
        {
            IAudioSessionManager2 manager = null;
            while (!exitEvent.WaitOne(500))
            {
                try
                {
                    if (manager == null)
                    {
                        IMMDevice device = FindOutput();
                        if (device == null) { otherSoundPlaying = false; continue; }
                        Guid g = typeof(IAudioSessionManager2).GUID;
                        object o;
                        Check(device.Activate(ref g, ClsctxAll, IntPtr.Zero, out o), "IAudioSessionManager2");
                        manager = (IAudioSessionManager2)o;
                        Release(device);
                    }
                    IAudioSessionEnumerator list;
                    Check(manager.GetSessionEnumerator(out list), "GetSessionEnumerator");
                    int count;
                    Check(list.GetCount(out count), "GetCount");
                    bool found = false;
                    uint foundProcess = 0;
                    for (int i = 0; i < count && !found; i++)
                    {
                        object o;
                        if (list.GetSession(i, out o) < 0) continue;
                        var session = (IAudioSessionControl2)o;
                        int state;
                        uint process;
                        if (session.GetState(out state) >= 0 && state == 1
                            && session.GetProcessId(out process) >= 0 && process != ownProcessId)
                        {
                            found = true;
                            foundProcess = process;
                        }
                        Release(session);
                    }
                    Release(list);
                    if (found != otherSoundPlaying)
                        Log(found ? "Sound playing (PID " + foundProcess + "), signal on." : "No sound playing.");
                    otherSoundPlaying = found;
                }
                catch
                {
                    Release(manager);
                    manager = null;
                    otherSoundPlaying = false;
                }
            }
        }

        // Finds the active output whose name is Output (for example "Speakers (High Definition Audio Device)"). With no
        // Output set, takes the first active output on the "High Definition Audio Device" adapter that is not headphones.
        static IMMDevice FindOutput()
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
            try
            {
                IMMDeviceCollection list;
                Check(enumerator.EnumAudioEndpoints(0, 1, out list), "EnumAudioEndpoints");
                uint count;
                Check(list.GetCount(out count), "GetCount");
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device;
                    if (list.Item(i, out device) < 0) continue;
                    string id;
                    if (device.GetId(out id) >= 0 && Matches(id))
                    {
                        Release(list);
                        return device;
                    }
                    Release(device);
                }
                Release(list);
                return null;
            }
            finally { Release(enumerator); }
        }

        static bool Matches(string id)
        {
            string description, adapter;
            ReadDeviceName(id, out description, out adapter);
            if (Output.Length > 0)
                return Output.Equals(description + " (" + adapter + ")", StringComparison.OrdinalIgnoreCase);
            return "High Definition Audio Device".Equals(adapter, StringComparison.OrdinalIgnoreCase)
                && description.IndexOf("Headphone", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // Reads an output's name from the Windows registry: description ("Speakers") and adapter name.
        static void ReadDeviceName(string id, out string description, out string adapter)
        {
            description = ""; adapter = "";
            int dot = id.LastIndexOf("}.", StringComparison.Ordinal);
            if (dot < 0) return;
            string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + id.Substring(dot + 2) + @"\Properties";
            using (var k = Registry.LocalMachine.OpenSubKey(key))
            {
                if (k == null) return;
                description = (k.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string) ?? "";
                adapter = (k.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string) ?? "";
            }
        }

        static void ReadSettings()
        {
            string path = Path.Combine(DataFolder, "settings.txt");
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (line.TrimStart().StartsWith("#") || eq < 0) continue;
                string name = line.Substring(0, eq).Trim().ToLowerInvariant();
                string value = line.Substring(eq + 1).Trim();
                double number;
                bool isNumber = double.TryParse(value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out number);
                if (name == "output") Output = value;
                else if (name == "frequency" && isNumber && number >= 1 && number <= 100) Frequency = number;
                else if (name == "targetdb" && isNumber && number <= -20) TargetDb = number;
                else if (name == "maxdb" && isNumber && number <= 0) MaxDb = number;
                else if (name == "holdseconds" && isNumber && number >= 0) HoldSeconds = number;
            }
        }

        static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(DataFolder);
                string path = Path.Combine(DataFolder, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 200000) File.Delete(path);
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
    }
}
