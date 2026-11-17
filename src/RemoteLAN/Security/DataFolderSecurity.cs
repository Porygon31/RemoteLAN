using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RemoteLAN.Security;

/// <summary>
/// The data folder holds saved passwords and the WebBridge private key,
/// so only SYSTEM and Administrators may access it (all RemoteLAN processes run as one of them).
/// </summary>
public static class DataFolderSecurity
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteLAN");

    public static void Restrict(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            // Listed before locking: a non-elevated caller (tests) loses list access once the DACL is applied.
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);

            var dirSec = new DirectorySecurity();
            dirSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // drop ProgramData's "Users: read"
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                dirSec.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }
            new DirectoryInfo(dir).SetAccessControl(dirSec);

            // Strip explicit ACEs (e.g. the old installer's "Users: full") so every child only inherits the rules above.
            foreach (string file in files)
            {
                var fileSec = new FileSecurity();
                fileSec.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                new FileInfo(file).SetAccessControl(fileSec);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Log($"[DataFolderSecurity] Failed to restrict '{dir}': {ex.Message}");
        }
    }
}
