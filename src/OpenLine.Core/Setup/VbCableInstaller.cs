using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace OpenLine.Core.Setup;

/// <summary>
/// VB-Cable is donationware and its license doesn't allow bundling it inside another installer,
/// so OpenLine downloads the official package from vb-audio.com, verifies VB-Audio's code
/// signature, and opens VB-Audio's own setup for the user to run.
/// </summary>
public static partial class VbCableInstaller
{
    public const string ProductPage = "https://vb-audio.com/Cable/index.htm";
    public const string DonatePage = "https://shop.vb-audio.com/en/win-apps/11-vb-cable.html";
    private const string FallbackZip = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip";
    private const string ExpectedSigner = "BUREL VINCENT"; // VB-Audio's EV code-signing subject

    [GeneratedRegex(@"https://download\.vb-audio\.com/Download_CABLE/VBCABLE_Driver_Pack(\d+)\.zip", RegexOptions.IgnoreCase)]
    private static partial Regex PackLink();

    /// <summary>Downloads and extracts the latest driver pack; returns the verified setup exe path.</summary>
    public static async Task<string> DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenLine/0.1 (+https://github.com/)");

        var zipUrl = await FindLatestPackAsync(http, ct) ?? FallbackZip;

        var work = Path.Combine(Path.GetTempPath(), "OpenLine", "vbcable");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, "pack.zip");

        using (var response = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        var extractDir = Path.Combine(work, "pack");
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        var exeName = Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe";
        var exe = Path.Combine(extractDir, exeName);
        if (!File.Exists(exe))
            throw new FileNotFoundException($"{exeName} wasn't in the VB-Cable package.");

        if (!IsSignedByVbAudio(exe))
            throw new InvalidDataException("The downloaded VB-Cable setup isn't signed by VB-Audio. It was not run.");

        return exe;
    }

    /// <summary>
    /// Opens VB-Audio's setup elevated and waits for it to close. The user clicks "Install Driver"
    /// there. Returns false if the UAC prompt was declined.
    /// </summary>
    public static async Task<bool> RunSetupAsync(string exe, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start VB-Cable setup.");
            await p.WaitForExitAsync(ct);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED (UAC declined)
        {
            return false;
        }
    }

    public static void RestartWindows() =>
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5 /d p:0:0") { UseShellExecute = false, CreateNoWindow = true });

    private static async Task<string?> FindLatestPackAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            var html = await http.GetStringAsync(ProductPage, ct);
            return PackLink().Matches(html)
                .Select(m => (Url: m.Value, Ver: int.Parse(m.Groups[1].Value)))
                .OrderByDescending(x => x.Ver)
                .Select(x => x.Url)
                .FirstOrDefault();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool IsSignedByVbAudio(string path)
    {
        if (!Authenticode.Verify(path)) return false;
        try
        {
#pragma warning disable SYSLIB0057 // no loader replacement for reading a PE signature
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains(ExpectedSigner, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>WinVerifyTrust: valid signature chained to a trusted root.</summary>
internal static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    public static bool Verify(string path)
    {
        var pathPtr = Marshal.StringToCoTaskMemUni(path);
        var filePtr = IntPtr.Zero;
        try
        {
            var file = new WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                pcwszFilePath = pathPtr,
            };
            filePtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(file, filePtr, false);

            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE (offline-friendly)
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 1,       // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x00001000 // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = GenericVerifyV2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            data.dwStateAction = 2;      // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result == 0;
        }
        finally
        {
            if (filePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(filePtr);
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }
}
