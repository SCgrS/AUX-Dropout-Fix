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
// click. Two things keep it from running dry: about 40 ms of signal are queued (80 ms for a while after a gap), and
// keeping the list of the other programs' sessions and writing the log happen on a thread of their own
// (SessionScanner). Gaps that still happen are counted and written to the log with what the signal thread was doing.
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
        // The stream's buffer can hold 200 ms, but only about QueueSeconds of it are kept filled: enough to ride out a
        // late wake-up, little enough that the signal still follows the Windows volume and the other sounds quickly.
        // After a gap the queue grows to LongQueueSeconds and stays so until LongQueueMinutes pass without one: on this
        // PC the signal thread was once held up for 50-60 ms while Spotify jumped to another part of a song. A longer
        // queue also means the signal reacts later to loud sound and to the Windows volume, so it is kept no longer
        // than needed.
        const long BufferDuration = 2000000;
        const double QueueSeconds = 0.04;
        const double LongQueueSeconds = 0.08;
        const double LongQueueMinutes = 5;
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
            IAudioClock audioClock = null;
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
                // The output's own meter shows the whole mix before the Windows volume, including sound from a session
                // that is not on the list yet. A meter in the hardware may read after the volume, so it is not used.
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
                uint normalQueue = Math.Min(bufferFrames, (uint)Math.Ceiling(rate * QueueSeconds));
                uint longQueue = Math.Max(normalQueue, Math.Min(bufferFrames * 3 / 4, (uint)Math.Ceiling(rate * LongQueueSeconds)));
                uint queueFrames = normalQueue;
                double longQueueUntil = 0;
                long defaultPeriod, minimumPeriod;
                if (client.GetDevicePeriod(out defaultPeriod, out minimumPeriod) < 0) defaultPeriod = 100000;
                double periodSeconds = defaultPeriod / 1e7;
                g = typeof(IAudioRenderClient).GUID;
                Check(client.GetService(ref g, out o), "GetService");
                renderer = (IAudioRenderClient)o;
                OptOutOfDucking(client);
                // The stream's clock, only for the gap report: how far the output got while we were away.
                g = typeof(IAudioClock).GUID;
                if (client.GetService(ref g, out o) >= 0) audioClock = o as IAudioClock;
                ulong clockFrequency;
                if (audioClock == null || audioClock.GetFrequency(out clockFrequency) < 0) clockFrequency = 0;

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
                level.QueueSeconds = (double)queueFrames / rate;
                double phase = 0, step = 2 * Math.PI * Frequency / rate;
                // The amplitude of the last sample written, and the ramp it is on.
                double current = 0, rampGoal = 0, rampStep = 0;
                int rampFrames = Math.Max(1, (int)(rate * RampSeconds));
                long silentFrames = 0;
                var clock = Stopwatch.StartNew();
                double lastSoundAt = -1e9, exitDeadline = 0, lastWriteAt = 0, lastPosition = -1;
                uint queuedAtLastWrite = 0;
                bool exiting = false;

                LogLater("Ready: " + rate + " Hz, " + channels + " channels, " + bits + " bit, buffer " + bufferFrames
                    + " frames, " + queueFrames + " kept queued, period "
                    + (defaultPeriod / 10000.0).ToString("0.##", CultureInfo.InvariantCulture) + " ms");
                while (true)
                {
                    double waitFrom = clock.Elapsed.TotalSeconds;
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
                        // While the stream is stopped, the scanner wakes us twice a second and when a new session turns
                        // up. Nothing else would notice then that the output went away.
                        if (WaitHandle.WaitAny(stoppedWait, 1000) == 0) return;
                        int deviceState;
                        uint idlePadding;
                        if (device.GetState(out deviceState) < 0 || deviceState != DeviceStateActive)
                            throw new Exception("The output is no longer available.");
                        Check(client.GetCurrentPadding(out idlePadding), "GetCurrentPadding");
                    }
                    double wokeUp = clock.Elapsed.TotalSeconds;

                    MeterSet fresh = scanner.TakeFresh();
                    if (fresh != null)
                    {
                        sessions = fresh;
                        scanner.HeldGeneration = fresh.Generation;
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
                    double metersRead = clock.Elapsed.TotalSeconds;
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
                        lastPosition = OutputSeconds(audioClock, clockFrequency);
                        LogLater(loudest >= 0 ? "Sound playing (PID " + sessions.Processes[loudest] + "), signal on." : "Signal on.");
                        continue;
                    }

                    uint padding;
                    Check(client.GetCurrentPadding(out padding), "GetCurrentPadding");
                    // Nothing of ours is left, and we woke up later than the queue lasted: Windows had to fill in silence
                    // for a moment, which is a click. Windows takes a whole period at a time, so the queue looks empty up
                    // to a period before it runs out: that much lateness is not counted.
                    // For the log: how long we waited for Windows to wake us, how long the meters took, and how far the
                    // output itself got meanwhile (if it got about as far as the time that passed, Windows kept playing and
                    // we were the late ones).
                    // Running out of silence does no harm (Windows fills in silence too), so that is not counted.
                    double wokeAt = clock.Elapsed.TotalSeconds;
                    if (padding == 0 && wokeAt - lastWriteAt > (double)queuedAtLastWrite / rate + periodSeconds
                        && silentFrames < queuedAtLastWrite)
                    {
                        double played = OutputSeconds(audioClock, clockFrequency) - lastPosition;
                        scanner.CountGap(wokeAt - lastWriteAt, wokeUp - waitFrom, metersRead - wokeUp, lastPosition >= 0 ? played : -1);
                        if (queueFrames < longQueue)
                        {
                            queueFrames = longQueue;
                            level.QueueSeconds = (double)queueFrames / rate;
                            LogLater("Keeping " + Math.Round(1000.0 * queueFrames / rate) + " ms queued for now.");
                        }
                        longQueueUntil = wokeAt + LongQueueMinutes * 60;
                    }
                    else if (queueFrames > normalQueue && wokeAt > longQueueUntil)
                    {
                        // The queue shrinks on its own: less is written until only normalQueue is left.
                        queueFrames = normalQueue;
                        level.QueueSeconds = (double)queueFrames / rate;
                        LogLater("No gaps for " + LongQueueMinutes + " minutes, back to " + Math.Round(1000.0 * queueFrames / rate) + " ms queued.");
                    }
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
                        // samples already queued still play at their level (40 ms, or 80 ms after a gap).
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
                        lastPosition = OutputSeconds(audioClock, clockFrequency);
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
                // The scanner owns the session meters and releases them itself.
                if (scanner != null) scanner.Dispose();
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                Release(audioClock); Release(mixMeter); Release(sessionManager); Release(renderer); Release(volume); Release(client); Release(device);
                if (bufferEvent != null) bufferEvent.Dispose();
                FlushLog();
            }
        }

        // How far the stream has played, in seconds, or -1 without a clock.
        static double OutputSeconds(IAudioClock audioClock, ulong frequency)
        {
            ulong position, qpc;
            if (audioClock == null || frequency == 0 || audioClock.GetPosition(out position, out qpc) < 0) return -1;
            return (double)position / frequency;
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
        // Our own signal can still be in the output meter's reading for the queue plus a meter period and some margin.
        public const double OwnMarginSeconds = 0.05;
        // The output's meter must show our own signal. On an output whose meter reads after the volume, or not at all,
        // it would always show much less: after DoubtSeconds of that, only the session meters are used.
        public const double DoubtSeconds = 3;

        readonly double ceiling, targetDb;
        // Length and highest amplitude of the recent fills, newest at wroteNext - 1.
        readonly double[] wroteSeconds = new double[128], wroteHighest = new double[128];
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

        // How much of the signal is kept queued right now (set by the audio code).
        public double QueueSeconds = 0.04;

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
        // reading. It includes the signal itself and also sound from a session that is not on the list yet. masterDb:
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
            double total = 0, own = 0, window = QueueSeconds + OwnMarginSeconds;
            for (int k = 1; k <= wroteSeconds.Length && total < window; k++)
            {
                int i = (wroteNext - k + wroteSeconds.Length) % wroteSeconds.Length;
                total += wroteSeconds[i];
                if (wroteHighest[i] > own) own = wroteHighest[i];
            }
            return own;
        }
    }

    // The peak meters of the other programs' sessions at one moment, with the process each belongs to. The meters
    // belong to the SessionScanner: a list owns nothing, and a meter is released only after the signal thread has
    // moved on to a list without it (Generation).
    sealed class MeterSet
    {
        public readonly IAudioMeterInformation[] Meters;
        public readonly uint[] Processes;
        public readonly int Generation;

        public MeterSet(IAudioMeterInformation[] meters, uint[] processes, int generation)
        {
            Meters = meters;
            Processes = processes;
            Generation = generation;
        }
    }

    // Keeps the list of the other programs' audio sessions on a thread of its own, and writes the log lines that the
    // signal thread leaves for it.
    //
    // Windows leaks a few kilobytes inside its audio code each time a program asks for the session list while it still
    // holds sessions from an earlier one (measured on this PC: about 3.7 KB a time, so ten times a second made the
    // program grow by about 2 MB a minute). So the list is asked for once, at the start. After that Windows reports new
    // sessions (IAudioSessionNotification), and a session that has ended (expired) is dropped. Asking a session for its
    // state does not leak.
    //
    // A session is dropped only once it has ended, or after it has not answered for a while. As a safety net the list
    // is read again every RefreshMinutes while the signal is off: an empty list is handed over first, and once the signal
    // thread reads it, everything is released and the list is read with nothing held, which does not leak.
    //
    // A new list is handed over with TakeFresh, and the signal thread says which list it reads with HeldGeneration.
    // This thread also owns the session manager and releases everything itself once it stops, so a call that is stuck
    // in the audio service never has something released under it.
    sealed class SessionScanner : IDisposable
    {
        const int CheckMilliseconds = 500;
        const int StreamingCheckMilliseconds = 100;
        const double GapReportSeconds = 60;
        const double RefreshMinutes = 5;
        const int MaxFailedChecks = 50;

        public readonly AutoResetEvent Scanned = new AutoResetEvent(false);
        // Written by the signal thread: whether its stream runs, the Windows volume for the gap report, and the list
        // it reads now.
        public volatile bool Streaming;
        public volatile float MasterDb;
        public volatile int HeldGeneration;

        readonly IAudioSessionManager2 manager;
        readonly int ownProcessId;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly AutoResetEvent arrived = new AutoResetEvent(false);
        readonly SessionArrivals arrivals;
        readonly Thread thread;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly object gapLock = new object();
        // The sessions held now. Windows hands out one wrapper per session, and each is held once.
        readonly List<IAudioSessionControl2> held = new List<IAudioSessionControl2>();
        readonly List<IAudioMeterInformation> heldMeters = new List<IAudioMeterInformation>();
        readonly List<uint> heldProcesses = new List<uint>();
        readonly List<int> heldFailures = new List<int>();
        // Ended sessions, each with the first list generation without it: released once the signal thread reads that.
        readonly List<KeyValuePair<object, int>> dropped = new List<KeyValuePair<object, int>>();
        bool registered, listed;
        MeterSet fresh;
        int generation, refreshGeneration;
        double listedAt;
        int gaps;
        long firstGapTicks;
        // The longest gap since the last report, in seconds: time since our last write, waiting for Windows, reading
        // the meters, and how far the output played meanwhile (-1 unknown).
        double worstLate, worstWait, worstMeters, worstPlayed;
        double lastGapReport = -1e9;
        string lastError;

        public SessionScanner(IAudioSessionManager2 manager, int ownProcessId)
        {
            this.manager = manager;
            this.ownProcessId = ownProcessId;
            arrivals = new SessionArrivals(arrived);
            thread = new Thread(Loop, 256 * 1024);
            thread.IsBackground = true;
            thread.Name = "Session watch";
            thread.Start();
        }

        public MeterSet TakeFresh()
        {
            return Interlocked.Exchange(ref fresh, null);
        }

        // Called by the signal thread when its queue ran dry. The lock is only ever held for a few instructions.
        public void CountGap(double late, double wait, double meters, double played)
        {
            lock (gapLock)
            {
                if (gaps == 0) firstGapTicks = DateTime.Now.Ticks;
                gaps++;
                if (late > worstLate)
                {
                    worstLate = late;
                    worstWait = wait;
                    worstMeters = meters;
                    worstPlayed = played;
                }
            }
        }

        void Loop()
        {
            try { Watching(); }
            finally
            {
                if (registered) { try { manager.UnregisterSessionNotification(arrivals); } catch { } }
                object o;
                while (arrivals.Queue.TryDequeue(out o)) Program.Release(o);
                ReleaseAll();
                Interlocked.Exchange(ref fresh, null);
                Program.Release(manager);
                ReportGaps(true);
                Program.FlushLog();
            }
        }

        void Watching()
        {
            var wake = new WaitHandle[] { stop, arrived };
            while (true)
            {
                try
                {
                    double now = clock.Elapsed.TotalSeconds;
                    if (refreshGeneration != 0)
                    {
                        // Sound started meanwhile: the full list again, the refresh waits for the next quiet spell.
                        if (Streaming)
                        {
                            refreshGeneration = 0;
                            listedAt = now;
                            Publish();
                        }
                        else if (HeldGeneration >= refreshGeneration)
                        {
                            ReleaseAll();
                            listed = false;
                            refreshGeneration = 0;
                        }
                    }
                    if (refreshGeneration == 0)
                    {
                        bool changed = false;
                        if (!listed)
                        {
                            changed = ListOnce();
                            listedAt = now;
                        }
                        changed |= TakeArrivals();
                        changed |= DropEnded();
                        if (changed || generation == 0) Publish();
                        ReleaseDropped();
                        if (!Streaming && now - listedAt > RefreshMinutes * 60)
                        {
                            generation++;
                            Interlocked.Exchange(ref fresh, new MeterSet(new IAudioMeterInformation[0], new uint[0], generation));
                            refreshGeneration = generation;
                        }
                    }
                    lastError = null;
                }
                catch (Exception e)
                {
                    // The signal thread notices on its own when the output goes away.
                    if (e.Message != lastError) Program.LogLater("Watching the sessions failed: " + e.Message);
                    lastError = e.Message;
                }
                Scanned.Set();
                ReportGaps(false);
                Program.FlushLog();
                if (WaitHandle.WaitAny(wake, Streaming ? StreamingCheckMilliseconds : CheckMilliseconds) == 0) return;
            }
        }

        // Registers for new sessions first, then goes through the list once, so that no session slips in between.
        // A session that turns up both ways is held only once.
        bool ListOnce()
        {
            if (!registered)
            {
                Program.Check(manager.RegisterSessionNotification(arrivals), "RegisterSessionNotification");
                registered = true;
            }
            IAudioSessionEnumerator list;
            Program.Check(manager.GetSessionEnumerator(out list), "GetSessionEnumerator");
            try
            {
                int count;
                Program.Check(list.GetCount(out count), "GetCount");
                for (int i = 0; i < count; i++)
                {
                    object o;
                    if (list.GetSession(i, out o) >= 0 && o != null) Add(o);
                }
            }
            finally { Program.Release(list); }
            listed = true;
            return true;
        }

        bool TakeArrivals()
        {
            bool changed = false;
            object o;
            while (arrivals.Queue.TryDequeue(out o))
            {
                int before = held.Count;
                Add(o);
                if (held.Count != before) changed = true;
            }
            return changed;
        }

        // Takes over one reference to a session. Keeps it if it belongs to another program and has not ended.
        // Inactive sessions are kept too: their meter reads 0 until they play again, and then the signal thread sees it
        // at once.
        void Add(object o)
        {
            var session = o as IAudioSessionControl2;
            var meter = o as IAudioMeterInformation;
            int state = 0, hr = 0;
            uint process = 0;
            if (session != null && meter != null && !held.Contains(session))
            {
                hr = session.GetState(out state);
                if (hr >= 0) hr = session.GetProcessId(out process);
            }
            if (session == null || meter == null || held.Contains(session) || hr < 0 || state == SessionExpired
                || process == ownProcessId)
            {
                if (hr < 0) Program.LogLater("A session could not be read and is left out (0x" + hr.ToString("X8") + ").");
                Program.Release(o);
                return;
            }
            held.Add(session);
            heldMeters.Add(meter);
            heldProcesses.Add(process);
            heldFailures.Add(0);
        }

        bool DropEnded()
        {
            bool changed = false;
            for (int i = held.Count - 1; i >= 0; i--)
            {
                int state;
                int hr = held[i].GetState(out state);
                if (hr >= 0 && state != SessionExpired)
                {
                    heldFailures[i] = 0;
                    continue;
                }
                if (hr < 0)
                {
                    // One failed answer does not end a session: it would never be reported again.
                    if (++heldFailures[i] < MaxFailedChecks) continue;
                    Program.LogLater("A session stopped answering and is dropped (0x" + hr.ToString("X8") + ").");
                }
                // Still in every list up to the current generation, not in the next one.
                dropped.Add(new KeyValuePair<object, int>(held[i], generation + 1));
                held.RemoveAt(i);
                heldMeters.RemoveAt(i);
                heldProcesses.RemoveAt(i);
                heldFailures.RemoveAt(i);
                changed = true;
            }
            return changed;
        }

        void ReleaseAll()
        {
            foreach (var session in held) Program.Release(session);
            foreach (var d in dropped) Program.Release(d.Key);
            held.Clear();
            heldMeters.Clear();
            heldProcesses.Clear();
            heldFailures.Clear();
            dropped.Clear();
        }

        // An older list the signal thread never took is simply dropped: it owns nothing.
        void Publish()
        {
            generation++;
            Interlocked.Exchange(ref fresh, new MeterSet(heldMeters.ToArray(), heldProcesses.ToArray(), generation));
        }

        void ReleaseDropped()
        {
            int reading = HeldGeneration;
            for (int i = dropped.Count - 1; i >= 0; i--)
            {
                if (reading < dropped[i].Value) continue;
                Program.Release(dropped[i].Key);
                dropped.RemoveAt(i);
            }
        }

        // At most one line a minute, so a bad stretch does not flood the log.
        void ReportGaps(bool final)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (!final && now - lastGapReport < GapReportSeconds) return;
            int count;
            long first;
            double late, wait, meters, played;
            lock (gapLock)
            {
                count = gaps;
                first = firstGapTicks;
                late = worstLate;
                wait = worstWait;
                meters = worstMeters;
                played = worstPlayed;
                gaps = 0;
                worstLate = 0;
            }
            if (count == 0) return;
            lastGapReport = now;
            Program.LogLater("Gap in the signal: " + count + (count == 1 ? " time" : " times")
                + (first > 0 ? ", first at " + new DateTime(first).ToString("HH:mm:ss") : "")
                + ", Windows volume " + MasterDb.ToString("0.0", CultureInfo.InvariantCulture) + " dB. Longest: "
                + Ms(late) + " since the last write (" + Ms(wait) + " waiting for Windows, " + Ms(meters) + " reading meters)"
                + (played >= 0 ? ", the output played " + Ms(played) + " meanwhile" : ""));
        }

        static string Ms(double seconds)
        {
            return (seconds * 1000).ToString("0.#", CultureInfo.InvariantCulture) + " ms";
        }

        // Stops the thread, which releases everything on its way out. If a call is stuck in the audio service, this
        // gives up waiting after a while and the thread cleans up whenever that call returns.
        public void Dispose()
        {
            stop.Set();
            if (!thread.Join(5000)) return;
            stop.Close();
            arrived.Close();
            Scanned.Close();
        }

        const int SessionExpired = 2;
    }

    // Windows calls this on a thread of its own when a program opens a new audio session on the output. It only hands
    // the session over: Windows asks for no waiting in here.
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    sealed class SessionArrivals : IAudioSessionNotification
    {
        public readonly ConcurrentQueue<object> Queue = new ConcurrentQueue<object>();
        readonly AutoResetEvent signal;

        public SessionArrivals(AutoResetEvent signal)
        {
            this.signal = signal;
        }

        public int OnSessionCreated(object newSession)
        {
            if (newSession != null)
            {
                Queue.Enqueue(newSession);
                try { signal.Set(); } catch (ObjectDisposedException) { }
            }
            return 0;
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

    [ComImport, Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClock
    {
        [PreserveSig] int GetFrequency(out ulong frequency);
        [PreserveSig] int GetPosition(out ulong position, out ulong qpcPosition);
        [PreserveSig] int GetCharacteristics(out uint characteristics);
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
        [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification notification);
        [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification notification);
    }

    [ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionNotification
    {
        [PreserveSig] int OnSessionCreated([MarshalAs(UnmanagedType.IUnknown)] object newSession);
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
