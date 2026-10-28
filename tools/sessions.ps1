# Lists the audio sessions on the default output (process, state, current peak). Read only.
# The peak is measured before the Windows volume is applied.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumeratorS {
  int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
  int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDeviceS endpoint);
}
[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceS {
  int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionManager2S {
  int GetAudioSessionControl(IntPtr g, int f, out IntPtr c);
  int GetSimpleAudioVolume(IntPtr g, int f, out IntPtr v);
  int GetSessionEnumerator(out IAudioSessionEnumeratorS e);
}
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionEnumeratorS {
  int GetCount(out int n);
  int GetSession(int i, [MarshalAs(UnmanagedType.IUnknown)] out object s);
}
[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl2S {
  int GetState(out int s);
  int GetDisplayName(out IntPtr n);
  int SetDisplayName(IntPtr n, IntPtr g);
  int GetIconPath(out IntPtr p);
  int SetIconPath(IntPtr p, IntPtr g);
  int GetGroupingParam(out Guid g);
  int SetGroupingParam(ref Guid g, IntPtr c);
  int RegisterAudioSessionNotification(IntPtr n);
  int UnregisterAudioSessionNotification(IntPtr n);
  int GetSessionIdentifier(out IntPtr s);
  int GetSessionInstanceIdentifier(out IntPtr s);
  int GetProcessId(out uint pid);
}
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioMeterInformationS { int GetPeakValue(out float peak); }
public static class Sessions {
  public static string List() {
    var en = (IMMDeviceEnumeratorS)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
    IMMDeviceS d; en.GetDefaultAudioEndpoint(0, 1, out d);
    Guid g = typeof(IAudioSessionManager2S).GUID; object o; d.Activate(ref g, 23, IntPtr.Zero, out o);
    IAudioSessionEnumeratorS e; ((IAudioSessionManager2S)o).GetSessionEnumerator(out e);
    int n; e.GetCount(out n);
    var sb = new System.Text.StringBuilder();
    for (int i = 0; i < n; i++) {
      object s; e.GetSession(i, out s);
      var c = (IAudioSessionControl2S)s;
      int st; c.GetState(out st); uint pid; c.GetProcessId(out pid);
      float peak; ((IAudioMeterInformationS)s).GetPeakValue(out peak);
      sb.AppendLine(pid + "|" + (st == 1 ? "active" : st == 0 ? "inactive" : "expired") + "|" + peak.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
    }
    return sb.ToString();
  }
}
'@
foreach ($line in ([Sessions]::List() -split "`r?`n" | Where-Object { $_ })) {
  $p = $line -split '\|'
  $name = if ($p[0] -eq '0') { 'System sounds' } else { (Get-Process -Id ([int]$p[0]) -ErrorAction SilentlyContinue).ProcessName }
  "{0,-8} {1,-20} {2,-9} peak {3}" -f $p[0], $name, $p[1], $p[2]
}
