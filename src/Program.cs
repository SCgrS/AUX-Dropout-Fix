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
// crackle, so the signal steps back while the other sound is loud (SignalLevel). The speaker does not mute then
// anyway.
//
// At low Windows volume the signal reaches the speaker about as loud as the music itself, so any gap in it is a loud
// click. Two things keep it from running dry: about 40 ms of signal are always queued, and the slower calls to the
// Windows audio service (looking for other programs' sessions) and writing the log run on a thread of their own
// (SessionScanner). Gaps that still happen are counted and written to the log.
//
// Built with the C# compiler that ships with Windows (C# 5, .NET Framework 4): Build.cmd.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

        // A session counts as playing only above this peak (-80 dB), so an open but silent stream does not turn the
        // signal on.
        const double AudiblePeak = 0.0001;
        // The stream's buffer can hold 100 ms, but only about QueueSeconds of it are kept filled: enough to ride out a
        // late wake-up, little enough that the signal still follows the Windows volume and the other sounds quickly.
        const long BufferDuration = 1000000;
        const double QueueSeconds = 0.04;
        // Any change of the signal's amplitude is spread over at least this long, however short the pieces of the
        // buffer that get filled are (Windows wakes us more often while another program uses a low-latency stream).
        const double RampSeconds = 0.01;
        // On exit the signal fades out first. If the queued audio has not played out by then, the stream stops anyway.
        const double ExitFadeLimitSeconds = 0.3;

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
                    FlushLog();
                    if (e.Message != lastError) Log("Error: " + e.Message);
                    lastError = e.Message;
                    exitEvent.WaitOne(errorWait);
                    errorWait = Math.Min(errorWait * 2, 60000);
                }
            }
            FlushLog();
            Log("Exited.");
            GC.KeepAlive(instanceLock);
            return 0;
        }

        // Streams the signal to one output while other programs play sound on it. Returns when exit is requested,
        // throws if the output goes away. Nothing here waits on the disk or on the session list: that is the
        // SessionScanner's job, so this thread can keep the queue filled.
        static void Run(IMMDevice device)
        {
            IAudioClient client = null;
            IAudioEndpointVolume volume = null;
            IAudioRenderClient renderer = null;
            IAudioSessionManager2 sessionManager = null;
            IAudioMeterInformation mixMeter = null;
            SessionScanner scanner = null;
            MeterSet sessions = null;
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
                // The output's own meter shows the whole mix before the Windows volume, including sound that started
                // since the last session scan. A meter in the hardware may read after the volume, so it is not used.
                // The signal works without it, so a failure here is not an error.
                uint hardware;
                if (volume.QueryHardwareSupport(out hardware) >= 0 && (hardware & HardwareMeter) == 0)
                {
                    g = typeof(IAudioMeterInformation).GUID;
                    if (device.Activate(ref g, ClsctxAll, IntPtr.Zero, out o) >= 0) mixMeter = o as IAudioMeterInformation;
                }

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

                // Event driven: Windows wakes us once per device period (10 ms) while the stream runs.
                Check(client.Initialize(0, AudclntStreamflagsEventcallback, BufferDuration, 0, format, IntPtr.Zero), "Initialize");
                bufferEvent = new AutoResetEvent(false);
                Check(client.SetEventHandle(bufferEvent.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
                uint bufferFrames;
                Check(client.GetBufferSize(out bufferFrames), "GetBufferSize");
                uint queueFrames = Math.Min(bufferFrames, (uint)Math.Ceiling(rate * QueueSeconds));
                long defaultPeriod, minimumPeriod;
                if (client.GetDevicePeriod(out defaultPeriod, out minimumPeriod) < 0) defaultPeriod = 100000;
                double periodSeconds = defaultPeriod / 1e7;
                g = typeof(IAudioRenderClient).GUID;
                Check(client.GetService(ref g, out o), "GetService");
                renderer = (IAudioRenderClient)o;
                OptOutOfDucking(client);

                mmcss = AvSetMmThreadCharacteristics("Audio", ref mmcssIndex);
                if (mmcss == IntPtr.Zero)
                    LogLater("The signal could not get audio priority (error " + Marshal.GetLastWin32Error() + ").");

                // From here on the scanner owns the session manager and releases it.
                scanner = new SessionScanner(sessionManager, ownProcessId);
                sessionManager = null;
                var floatSamples = new float[bufferFrames * channels];
                var shortSamples = new short[bufferFrames * channels];
                var runningWait = new WaitHandle[] { exitEvent, bufferEvent };
                var stoppedWait = new WaitHandle[] { exitEvent, scanner.Scanned };
                var drainWait = new WaitHandle[] { bufferEvent };
                var level = new SignalLevel(MaxDb, TargetDb);
                double phase = 0, step = 2 * Math.PI * Frequency / rate;
                // The amplitude of the last sample written, and the ramp it is on.
                double current = 0, rampGoal = 0, rampStep = 0;
                int rampFrames = Math.Max(1, (int)(rate * RampSeconds));
                long silentFrames = 0;
                var clock = Stopwatch.StartNew();
                double lastSoundAt = -1e9, exitDeadline = 0, lastWriteAt = 0;
                uint queuedAtLastWrite = 0;
                bool exiting = false;

                LogLater("Ready: " + rate + " Hz, " + channels + " channels, " + bits + " bit, buffer " + bufferFrames
                    + " frames, " + queueFrames + " kept queued, period "
                    + (defaultPeriod / 10000.0).ToString("0.##", CultureInfo.InvariantCulture) + " ms");
                while (true)
                {
                    if (started)
                    {
                        int woken = WaitHandle.WaitAny(exiting ? drainWait : runningWait, 2000);
                        if (woken == WaitHandle.WaitTimeout) throw new Exception("Audio device is not responding.");
                        if (!exiting && woken == 0)
                        {
                            exiting = true;
                            exitDeadline = clock.Elapsed.TotalSeconds + ExitFadeLimitSeconds;
                        }
                    }
                    else
                    {
                        // While the stream is stopped, the scanner wakes us twice a second. Nothing else would notice
                        // then that the output went away, and the signal would come on late once sound starts.
                        if (WaitHandle.WaitAny(stoppedWait, 1000) == 0) return;
                        int deviceState;
                        uint idlePadding;
                        if (device.GetState(out deviceState) < 0 || deviceState != DeviceStateActive)
                            throw new Exception("The output is no longer available.");
                        Check(client.GetCurrentPadding(out idlePadding), "GetCurrentPadding");
                    }

                    MeterSet fresh = scanner.TakeFresh();
                    if (fresh != null)
                    {
                        scanner.Retire(sessions);
                        sessions = fresh;
                    }

                    // The other programs' peaks over the last 10 ms, measured before the Windows volume.
                    double now = clock.Elapsed.TotalSeconds;
                    double sessionsPeak = 0;
                    int loudest = -1;
                    float loudestPeak = 0;
                    if (sessions != null)
                    {
                        for (int i = 0; i < sessions.Meters.Length; i++)
                        {
                            float peak;
                            if (sessions.Meters[i].GetPeakValue(out peak) < 0) continue;
                            sessionsPeak += peak;
                            if (peak > loudestPeak) { loudestPeak = peak; loudest = i; }
                        }
                    }
                    bool audible = sessionsPeak > AudiblePeak;
                    if (audible) lastSoundAt = now;
                    bool open = !exiting && (audible || now - lastSoundAt < HoldSeconds);

                    if (!started)
                    {
                        if (!open) continue;
                        // The queue is filled with silence before the stream starts, so it cannot run dry right away.
                        IntPtr silence;
                        Check(renderer.GetBuffer(queueFrames, out silence), "GetBuffer");
                        Check(renderer.ReleaseBuffer(queueFrames, AudclntBufferflagsSilent), "ReleaseBuffer");
                        Check(client.Start(), "Start");
                        started = true;
                        scanner.Streaming = true;
                        level.Reset();
                        phase = 0;
                        current = rampGoal = rampStep = 0;
                        silentFrames = queueFrames;
                        lastWriteAt = clock.Elapsed.TotalSeconds;
                        queuedAtLastWrite = queueFrames;
                        LogLater(loudest >= 0 ? "Sound playing (PID " + sessions.Processes[loudest] + "), signal on." : "Signal on.");
                        continue;
                    }

                    uint padding;
                    Check(client.GetCurrentPadding(out padding), "GetCurrentPadding");
                    // Nothing of ours is left, and we woke up later than the queue lasted: Windows had to fill in silence
                    // for a moment, which is a click. Windows takes a whole period at a time, so the queue looks empty up
                    // to a period before it runs out: that much lateness is not counted.
                    double wokeAt = clock.Elapsed.TotalSeconds;
                    if (padding == 0 && wokeAt - lastWriteAt > (double)queuedAtLastWrite / rate + periodSeconds) scanner.CountGap();
                    uint free = padding < queueFrames ? queueFrames - padding : 0;
                    if (free > 0)
                    {
                        double start = current, target;
                        if (exiting) target = level.FadeOut();
                        else
                        {
                            float masterDb, mixPeak;
                            Check(volume.GetMasterVolumeLevel(out masterDb), "GetMasterVolumeLevel");
                            if (mixMeter == null || mixMeter.GetPeakValue(out mixPeak) < 0) mixPeak = -1;
                            scanner.MasterDb = masterDb;
                            target = level.Next((double)free / rate, open, sessionsPeak, mixPeak, masterDb);
                            if (level.MeterIgnored && mixMeter != null)
                            {
                                LogLater("The output's meter does not show the signal, so only the session meters are used.");
                                Release(mixMeter);
                                mixMeter = null;
                            }
                        }

                        // Each change is spread over RampSeconds, and the ramp carries on into the next fill. The older
                        // samples already queued still play at their level, up to QueueSeconds.
                        if (target != rampGoal)
                        {
                            rampGoal = target;
                            rampStep = Math.Abs(target - current) / rampFrames;
                        }
                        double highest = current;
                        IntPtr data;
                        Check(renderer.GetBuffer(free, out data), "GetBuffer");
                        for (int i = 0; i < free; i++)
                        {
                            if (current < target) current = Math.Min(target, current + rampStep);
                            else if (current > target) current = Math.Max(target, current - rampStep);
                            if (current > highest) highest = current;
                            double s = current * Math.Sin(phase);
                            phase += step;
                            if (phase >= 2 * Math.PI) phase -= 2 * Math.PI;
                            for (int c = 0; c < channels; c++)
                            {
                                if (isFloat) floatSamples[i * channels + c] = (float)s;
                                else shortSamples[i * channels + c] = (short)(s * 32767);
                            }
                        }
                        level.Wrote((double)free / rate, highest);
                        silentFrames = start == 0 && target == 0 ? silentFrames + free : 0;
                        if (isFloat) Marshal.Copy(floatSamples, 0, data, (int)free * channels);
                        else Marshal.Copy(shortSamples, 0, data, (int)free * channels);
                        Check(renderer.ReleaseBuffer(free, 0), "ReleaseBuffer");
                        lastWriteAt = clock.Elapsed.TotalSeconds;
                        queuedAtLastWrite = padding + free;
                    }

                    // The signal is off and everything still queued is silence: stop the stream (or, on exit, return).
                    if (!open && silentFrames >= padding + free)
                    {
                        if (exiting) return;
                        Check(client.Stop(), "Stop");
                        Check(client.Reset(), "Reset");
                        started = false;
                        scanner.Streaming = false;
                        LogLater("No sound playing, signal off.");
                    }
                    else if (exiting && clock.Elapsed.TotalSeconds > exitDeadline) return;
                }
            }
            finally
            {
                if (started) { try { client.Stop(); } catch { } }
                if (mmcss != IntPtr.Zero) { try { AvRevertMmThreadCharacteristics(mmcss); } catch { } }
                if (scanner != null)
                {
                    scanner.Retire(sessions);
                    scanner.Dispose();
                }
                else if (sessions != null) sessions.Release();
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                Release(mixMeter); Release(sessionManager); Release(renderer); Release(volume); Release(client); Release(device);
                if (bufferEvent != null) bufferEvent.Dispose();
                FlushLog();
            }
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
                    bool isNumber = double.TryParse(value.Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out number);
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

        static readonly object logLock = new object();
        static readonly ConcurrentQueue<KeyValuePair<DateTime, string>> laterLines =
            new ConcurrentQueue<KeyValuePair<DateTime, string>>();

        internal static void Log(string message)
        {
            Write(DateTime.Now, message);
        }

        // For the signal thread: the line keeps the time it was logged, but the SessionScanner writes it to the disk, so
        // a slow disk cannot hold up the signal.
        internal static void LogLater(string message)
        {
            laterLines.Enqueue(new KeyValuePair<DateTime, string>(DateTime.Now, message));
        }

        internal static void FlushLog()
        {
            KeyValuePair<DateTime, string> line;
            while (laterLines.TryDequeue(out line)) Write(line.Key, line.Value);
        }

        static void Write(DateTime time, string message)
        {
            lock (logLock)
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
                    File.AppendAllText(path, time.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }

        internal static void Check(int hr, string what)
        {
            if (hr < 0) throw new Exception(what + " failed (0x" + hr.ToString("X8") + ")");
        }

        internal static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) { try { Marshal.ReleaseComObject(o); } catch { } }
        }

        const int ClsctxAll = 23;
        const int DeviceStateActive = 1;
        const uint AudclntStreamflagsEventcallback = 0x00040000;
        const uint AudclntBufferflagsSilent = 0x2;
        const uint HardwareMeter = 0x4;

        [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr AvSetMmThreadCharacteristics(string task, ref uint index);
        [DllImport("avrt.dll")]
        static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
    }

    // Works out the signal's amplitude for each piece of the buffer that gets filled. Kept apart from the audio code,
    // so it can be tried out with made-up readings.
    sealed class SignalLevel
    {
        // The other programs' sound plus the signal is kept below this (full scale is 1).
        public const double MixCeiling = 0.9;
        // The loudest peak of the other sound is held for PeakHoldSeconds after it was last reached, then comes down by
        // PeakRelease per second (full scale per second). Music has its loud notes every second or so: the signal stays
        // down between them instead of coming back up just in time for the next one. Once it gets quiet, the signal is
        // back within about three seconds, and the speaker needs about ten quiet seconds before it mutes.
        public const double PeakHoldSeconds = 2;
        public const double PeakRelease = 0.5;
        public const double PeakRenew = 0.75;

        // A session's meter reads before the program's own volume control (Spotify's volume slider, for example), so
        // it can show the sound much louder than it reaches the output. Ratio is how much of the sessions' level
        // actually reaches the mix: the highest value seen recently, coming down by RatioRelease per second so that it
        // follows a volume turned down. It starts at 0 and is learned upward: right after the stream starts the signal is
        // still near 0, so the output's meter shows the other sound alone. Each estimate uses the previous fill's mix
        // reading and the highest session reading around it, in case the two meters were read an engine period apart.
        public const double RatioRelease = 0.2;
        // Above this, Windows' own limiter may be holding the mix down: the output's meter cannot tell by how much it is
        // over, so the signal steps back at once, the harder the further over.
        public const double SaturatedMix = 0.95;
        // Our own signal can still be in the output meter's reading for the queue plus one meter period.
        public const double OwnWindowSeconds = 0.1;
        // The output's meter must show our own signal. On an output whose meter reads after the volume, or not at all,
        // it would always show much less: after DoubtSeconds of that, only the session meters are used.
        public const double DoubtSeconds = 3;

        readonly double ceiling, targetDb;
        // Length and highest amplitude of the recent fills, newest at wroteNext - 1.
        readonly double[] wroteSeconds = new double[64], wroteHighest = new double[64];
        int wroteNext;
        // The last three fills' readings, the newest at index newest. readings counts them up to 2.
        readonly double[] lastMix = new double[3], lastOwn = new double[3], lastSessions = new double[3];
        int newest, readings;
        double gate, heldPeak, heldFor, ratio, doubtedFor;
        bool meterIgnored;

        public SignalLevel(double maxDb, double targetDb)
        {
            ceiling = Math.Pow(10, maxDb / 20.0);
            this.targetDb = targetDb;
        }

        // True once the output's meter has turned out not to show our own signal.
        public bool MeterIgnored { get { return meterIgnored; } }

        public void Reset()
        {
            gate = 0;
            heldPeak = 0;
            heldFor = 0;
            ratio = 0;
            newest = 0;
            readings = 0;
            doubtedFor = 0;
            Array.Clear(wroteSeconds, 0, wroteSeconds.Length);
            Array.Clear(wroteHighest, 0, wroteHighest.Length);
            Array.Clear(lastMix, 0, 3);
            Array.Clear(lastOwn, 0, 3);
            Array.Clear(lastSessions, 0, 3);
        }

        // seconds: how much audio this fill covers. open: whether the signal should be on. sessionsPeak: the sum of the
        // other programs' session peaks. mixPeak: the output meter's peak of the whole mix, or below 0 without a
        // reading. It includes the signal itself and also sounds that started after the last session scan. masterDb:
        // the Windows volume. Returns the amplitude to go to.
        public double Next(double seconds, bool open, double sessionsPeak, double mixPeak, double masterDb)
        {
            gate = open ? Math.Min(1, gate + seconds / 0.5) : Math.Max(0, gate - seconds / 2.0);

            double others = sessionsPeak;
            bool useMeter = mixPeak >= 0 && !meterIgnored;
            double own = useMeter ? Own() : 0;
            // While our signal is clearly there, the meter has to show at least half of it. It shows less for a moment
            // while the signal steps down (own still remembers the higher level), so only a doubt that lasts counts. With
            // the signal low, the meter can neither be confirmed nor doubted.
            if (useMeter && own > 0.2)
            {
                if (mixPeak < 0.5 * own)
                {
                    doubtedFor += seconds;
                    if (doubtedFor > DoubtSeconds) meterIgnored = true;
                }
                else doubtedFor = 0;
            }
            // A meter in doubt is left out: the session meters alone, as without it.
            if (doubtedFor > 0.3) useMeter = false;
            if (useMeter && doubtedFor > 0) others = sessionsPeak * ratio;
            else if (useMeter)
            {
                // Our own signal can have added at most `own` to the mix's peak, or taken at most `own` away from it.
                double atLeast = mixPeak - own;
                int previous = newest;
                newest = (newest + 1) % 3;
                lastMix[newest] = mixPeak;
                lastOwn[newest] = own;
                lastSessions[newest] = sessionsPeak;
                if (readings < 2) readings++;
                double sessionsAround = Math.Max(lastSessions[0], Math.Max(lastSessions[1], lastSessions[2]));
                if (readings >= 2 && sessionsAround > 0.05)
                {
                    double seen = (lastMix[previous] - lastOwn[previous]) / sessionsAround;
                    ratio = Math.Max(Math.Max(0, Math.Min(1, seen)), ratio - seconds * RatioRelease);
                }
                others = sessionsPeak * ratio;
                if (mixPeak < SaturatedMix) others = Math.Min(others, mixPeak + own);
                others = Math.Max(others, atLeast);
                if (mixPeak >= SaturatedMix) others = Math.Max(others, Math.Min(1, heldPeak + 2 * (mixPeak - MixCeiling)));
            }
            // A peak that comes back almost as loud keeps the hold going: the readings scatter a little from note to note.
            if (others >= heldPeak)
            {
                heldPeak = others;
                heldFor = 0;
            }
            else if (others >= heldPeak * PeakRenew) heldFor = 0;
            else
            {
                heldFor += seconds;
                if (heldFor > PeakHoldSeconds) heldPeak = Math.Max(others, heldPeak - seconds * PeakRelease);
            }

            double room = Math.Max(0, MixCeiling - heldPeak);
            return gate * Math.Min(Math.Min(ceiling, room), Math.Pow(10, (targetDb - masterDb) / 20.0));
        }

        // On exit: down to zero.
        public double FadeOut()
        {
            gate = 0;
            return 0;
        }

        // Called after each fill with its length and the highest amplitude written in it.
        public void Wrote(double seconds, double highest)
        {
            wroteSeconds[wroteNext] = seconds;
            wroteHighest[wroteNext] = highest;
            wroteNext = (wroteNext + 1) % wroteSeconds.Length;
        }

        // The most our signal can be adding to what the output's meter shows right now.
        double Own()
        {
            double total = 0, own = 0;
            for (int k = 1; k <= wroteSeconds.Length && total < OwnWindowSeconds; k++)
            {
                int i = (wroteNext - k + wroteSeconds.Length) % wroteSeconds.Length;
                total += wroteSeconds[i];
                if (wroteHighest[i] > own) own = wroteHighest[i];
            }
            return own;
        }
    }

    // The peak meters of the other programs' sessions at one moment, with the process each belongs to.
    sealed class MeterSet
    {
        public readonly IAudioMeterInformation[] Meters;
        public readonly uint[] Processes;

        public MeterSet(IAudioMeterInformation[] meters, uint[] processes)
        {
            Meters = meters;
            Processes = processes;
        }

        public void Release()
        {
            foreach (var meter in Meters) Program.Release(meter);
        }
    }

    // Looks for the other programs' audio sessions on a thread of its own, and writes the log lines that the signal
    // thread leaves for it. Going through the session list means several calls to the Windows audio service, which can
    // be slow while that service is busy (for example while another program opens its stream). While the signal plays,
    // it looks ten times a second, so a program that opens a new session is soon measured on its own. While the stream
    // is stopped, twice a second is enough to notice sound starting.
    //
    // A new list is handed over with TakeFresh. The signal thread gives the list it no longer reads back with Retire,
    // and only this thread releases lists, so a meter is never released while the signal thread may still read it.
    // This thread also owns the session manager and releases everything itself once it stops, so a scan that is stuck
    // in a call to the audio service never has something released under it.
    sealed class SessionScanner : IDisposable
    {
        const int ScanMilliseconds = 500;
        const int StreamingScanMilliseconds = 100;
        const double GapReportSeconds = 60;

        public readonly AutoResetEvent Scanned = new AutoResetEvent(false);
        // Written by the signal thread: whether its stream runs, and the Windows volume for the gap report.
        public volatile bool Streaming;
        public volatile float MasterDb;

        readonly IAudioSessionManager2 manager;
        readonly int ownProcessId;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly ConcurrentQueue<MeterSet> retired = new ConcurrentQueue<MeterSet>();
        readonly Thread thread;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly object gapLock = new object();
        MeterSet fresh;
        int gaps;
        long firstGapTicks;
        double lastGapReport = -1e9;
        string lastError;

        public SessionScanner(IAudioSessionManager2 manager, int ownProcessId)
        {
            this.manager = manager;
            this.ownProcessId = ownProcessId;
            thread = new Thread(Loop, 256 * 1024);
            thread.IsBackground = true;
            thread.Name = "Session scan";
            thread.Start();
        }

        public MeterSet TakeFresh()
        {
            return Interlocked.Exchange(ref fresh, null);
        }

        public void Retire(MeterSet set)
        {
            if (set != null) retired.Enqueue(set);
        }

        // Called by the signal thread when its queue ran dry. The lock is only ever held for a few instructions.
        public void CountGap()
        {
            lock (gapLock)
            {
                if (gaps == 0) firstGapTicks = DateTime.Now.Ticks;
                gaps++;
            }
        }

        void Loop()
        {
            try { Scanning(); }
            finally
            {
                ReleaseRetired();
                MeterSet unused = Interlocked.Exchange(ref fresh, null);
                if (unused != null) unused.Release();
                Program.Release(manager);
                ReportGaps(true);
                Program.FlushLog();
            }
        }

        void Scanning()
        {
            do
            {
                ReleaseRetired();
                try
                {
                    MeterSet set = Scan();
                    lastError = null;
                    // A list the signal thread never took is released here.
                    MeterSet unused = Interlocked.Exchange(ref fresh, set);
                    if (unused != null) unused.Release();
                }
                catch (Exception e)
                {
                    // The signal thread notices on its own when the output goes away.
                    if (e.Message != lastError) Program.LogLater("Looking for sessions failed: " + e.Message);
                    lastError = e.Message;
                }
                Scanned.Set();
                ReportGaps(false);
                Program.FlushLog();
            } while (!stop.WaitOne(Streaming ? StreamingScanMilliseconds : ScanMilliseconds));
        }

        MeterSet Scan()
        {
            var meters = new List<IAudioMeterInformation>();
            var processes = new List<uint>();
            IAudioSessionEnumerator list;
            Program.Check(manager.GetSessionEnumerator(out list), "GetSessionEnumerator");
            try
            {
                int count;
                Program.Check(list.GetCount(out count), "GetCount");
                for (int i = 0; i < count; i++)
                {
                    object o;
                    if (list.GetSession(i, out o) < 0 || o == null) continue;
                    var session = o as IAudioSessionControl2;
                    var meter = o as IAudioMeterInformation;
                    int state;
                    uint process;
                    // Inactive sessions (0) are kept too: their meter reads 0 until they play again, and then the
                    // signal thread sees it at once rather than at the next scan. Expired sessions (2) are left out.
                    if (session != null && meter != null && session.GetState(out state) >= 0 && state != 2
                        && session.GetProcessId(out process) >= 0 && process != ownProcessId)
                    {
                        meters.Add(meter);
                        processes.Add(process);
                    }
                    else Program.Release(o);
                }
            }
            catch
            {
                foreach (var meter in meters) Program.Release(meter);
                throw;
            }
            finally { Program.Release(list); }
            return new MeterSet(meters.ToArray(), processes.ToArray());
        }

        void ReleaseRetired()
        {
            MeterSet set;
            while (retired.TryDequeue(out set)) set.Release();
        }

        // At most one line a minute, so a bad stretch does not flood the log.
        void ReportGaps(bool final)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (!final && now - lastGapReport < GapReportSeconds) return;
            int count;
            long first;
            lock (gapLock)
            {
                count = gaps;
                first = firstGapTicks;
                gaps = 0;
            }
            if (count == 0) return;
            lastGapReport = now;
            Program.LogLater("Gap in the signal: " + count + (count == 1 ? " time" : " times")
                + (first > 0 ? ", first at " + new DateTime(first).ToString("HH:mm:ss") : "")
                + ", Windows volume " + MasterDb.ToString("0.0", CultureInfo.InvariantCulture) + " dB");
        }

        // Stops the thread, which releases everything on its way out. If a scan is stuck in a call to the audio service,
        // this gives up waiting after a while and the thread cleans up whenever that call returns.
        public void Dispose()
        {
            stop.Set();
            if (!thread.Join(5000)) return;
            stop.Close();
            Scanned.Close();
        }
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
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid context);
        [PreserveSig] int VolumeStepDown(ref Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
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
