using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RemoteLAN.Security;

namespace RemoteLAN.Recording;

internal static class RecordingLocation
{
    private const int MaxRemoteNameLength = 40;
    private static readonly Guid FolderIdVideos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static string CreateOutputPath(string remoteName)
    {
        string folder = Path.Combine(GetVideosFolder(), "RemoteLAN");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, BuildFileName(remoteName, DateTime.Now));
    }

    /// <summary>
    /// The remote name comes from an unauthenticated discovery packet, so it must never shape the path.
    /// The random suffix matters too: SYSTEM writes in a folder the user controls, and a link planted
    /// under a predictable name could redirect the write onto an existing file.
    /// </summary>
    internal static string BuildFileName(string remoteName, DateTime now)
    {
        var invalid = Path.GetInvalidFileNameChars().Append('.').ToHashSet();
        string safeName = new string(remoteName.Take(MaxRemoteNameLength).Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        if (safeName.Length == 0) safeName = "session";

        return $"RemoteLAN_{safeName}_{now:yyyyMMdd_HHmmss}_{RandomNumberGenerator.GetHexString(6, lowercase: true)}.mp4";
    }

    private static string GetVideosFolder()
    {
        if (!DesktopManager.IsSystem)
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos, Environment.SpecialFolderOption.Create);
        }

        // Under SYSTEM, MyVideos is the SYSTEM profile, which the signed-in user cannot open
        return GetSignedInUserVideosFolder()
            ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonVideos, Environment.SpecialFolderOption.Create);
    }

    private static string? GetSignedInUserVideosFolder()
    {
        if (!WTSQueryUserToken((uint)Process.GetCurrentProcess().SessionId, out IntPtr token))
        {
            DiagnosticLogger.Log($"[RecordingLocation] WTSQueryUserToken failed: {Marshal.GetLastWin32Error()}");
            return null;
        }

        try
        {
            int hr = SHGetKnownFolderPath(FolderIdVideos, 0, token, out IntPtr path);
            try
            {
                if (hr == 0) return Marshal.PtrToStringUni(path);
                DiagnosticLogger.Log($"[RecordingLocation] SHGetKnownFolderPath failed: 0x{hr:X8}");
                return null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
