using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using OpenLine.Core.Settings;

namespace OpenLine.Core.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsVbCable);

public sealed record AudioAppInfo(string ProcessName, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>Thin wrapper over the MMDevice API. Call from the engine thread.</summary>
internal sealed class DeviceCatalog(MMDeviceEnumerator enumerator)
{
    public IReadOnlyList<AudioDeviceInfo> List(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();
        using var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        foreach (var d in devices)
        {
            using (d)
            {
                var name = d.FriendlyName;
                result.Add(new AudioDeviceInfo(d.ID, name, IsVbCableName(name)));
            }
        }
        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Resolves an id (or the default) to an active endpoint. Null if unavailable.</summary>
    public MMDevice? Resolve(DataFlow flow, string? id)
    {
        if (DeviceIds.IsDefault(id))
            return enumerator.TryGetDefaultAudioEndpoint(flow, Role.Console, out var def) ? def : null;

        try
        {
            var device = enumerator.GetDevice(id!);
            if (device.State == DeviceState.Active && device.DataFlow == flow) return device;
            device.Dispose();
        }
        catch (Exception)
        {
            // Unknown id (device uninstalled) — fall through.
        }
        return null;
    }

    /// <summary>Active = usable. Disabled = installed but turned off in Sound settings.</summary>
    public VbCablePresence GetVbCablePresence()
    {
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active | DeviceState.Disabled);
        var presence = VbCablePresence.Missing;
        foreach (var d in devices)
        {
            using (d)
            {
                if (!IsVbCableName(d.FriendlyName)) continue;
                if (d.State == DeviceState.Active) return VbCablePresence.Ready;
                presence = VbCablePresence.Disabled;
            }
        }
        return presence;
    }

    // "CABLE Input (VB-Audio Virtual Cable)" is the playback side apps send into.
    private static bool IsVbCableName(string name) =>
        name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) &&
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);
}

public enum VbCablePresence { Missing, Disabled, Ready }

/// <summary>Finds apps that have an audio session, and resolves them back to a process tree.</summary>
internal static class AppCatalog
{
    public static IReadOnlyList<AudioAppInfo> List(MMDeviceEnumerator enumerator)
    {
        var self = Environment.ProcessId;
        var pids = new HashSet<int>();
        using (var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            foreach (var d in devices)
            {
                using (d)
                {
                    try
                    {
                        var manager = d.AudioSessionManager;
                        manager.RefreshSessions();
                        var sessions = manager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            using var s = sessions[i];
                            if (s.IsSystemSoundsSession) continue;
                            var pid = (int)s.GetProcessID;
                            if (pid != 0 && pid != self) pids.Add(pid);
                        }
                    }
                    catch (Exception)
                    {
                        // Some endpoints refuse session enumeration; skip them.
                    }
                }
            }
        }

        var apps = new Dictionary<string, AudioAppInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                var name = p.ProcessName;
                if (apps.ContainsKey(name)) continue;
                apps[name] = new AudioAppInfo(name, Describe(p));
            }
            catch (Exception)
            {
                // Process exited between enumeration and lookup.
            }
        }
        return apps.Values.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Picks the root process for a name (e.g. the main chrome.exe, not a renderer) so that
    /// "include process tree" captures every child that might be playing audio.
    /// </summary>
    public static int? ResolveRootProcessId(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        try
        {
            if (procs.Length == 0) return null;
            var ids = procs.Select(p => p.Id).ToHashSet();
            foreach (var p in procs)
            {
                var parent = ProcessTree.GetParentId(p.Id);
                if (parent is null || !ids.Contains(parent.Value)) return p.Id;
            }
            return procs[0].Id;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private static string Describe(Process p)
    {
        try
        {
            var desc = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
        }
        catch (Exception)
        {
            // Elevated / protected process: fall back to the exe name.
        }
        return p.ProcessName;
    }
}

internal static partial class ProcessTree
{
    private const int ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(IntPtr process, int infoClass, out ProcessBasicInformation info, int size, out int returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    public static int? GetParentId(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int status = NtQueryInformationProcess(h, 0, out var info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
            return status == 0 ? (int)info.InheritedFromUniqueProcessId : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (Exception)
        {
            return true; // access denied etc. — assume alive
        }
    }
}
