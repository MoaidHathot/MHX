using System.Security.Cryptography;
using System.Text.Json;

namespace MHX.Distribution;

internal static class AtomicInstall
{
    internal const string JournalName = ".mhx-recovery.json";

    public static FileStream Lock(string directory)
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, ".mhx-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new MhxException("Another MHX installation or update is in progress."); }
    }

    public static string Hash(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    public static void Recover(string directory)
    {
        string journal = Path.Combine(directory, JournalName);
        if (!File.Exists(journal)) return;
        if (new FileInfo(journal).Length > 8192) throw new MhxException("Invalid update recovery record.");
        RecoveryRecord record = JsonSerializer.Deserialize(File.ReadAllText(journal), ReleaseJsonContext.Default.RecoveryRecord)
            ?? throw new MhxException("Invalid update recovery record.");
        if (string.IsNullOrEmpty(record.Target) || Path.GetFileName(record.Target) != record.Target || record.Target is "." or ".." ||
            string.IsNullOrEmpty(record.Backup) || Path.GetFileName(record.Backup) != record.Backup || !record.Backup.StartsWith(".mhx-backup-", StringComparison.Ordinal) ||
            !ReleaseValidation.ValidHash(record.OldHash) || !ReleaseValidation.ValidHash(record.NewHash))
            throw new MhxException("Invalid update recovery paths or hashes.");
        string target = Path.Combine(directory, record.Target);
        string backup = Path.Combine(directory, record.Backup);
        if (!File.Exists(target))
        {
            if (!File.Exists(backup) || !Hash(backup).Equals(record.OldHash, StringComparison.OrdinalIgnoreCase))
                throw new MhxException("Interrupted update requires manual recovery; its backup is not intact.");
            File.Move(backup, target);
        }
        string current = Hash(target);
        if (!current.Equals(record.OldHash, StringComparison.OrdinalIgnoreCase) && !current.Equals(record.NewHash, StringComparison.OrdinalIgnoreCase))
            throw new MhxException("Interrupted update requires manual recovery; the installed file has changed.");
        File.Delete(journal);
    }

    public static void Replace(string staged, string target, string expectedHash)
    {
        if (!ReleaseValidation.ValidHash(expectedHash)) throw new MhxException("Invalid expected download hash.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(target))!;
        if (!Path.GetDirectoryName(Path.GetFullPath(staged))!.Equals(directory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new MhxException("Updates must be staged on the destination filesystem.");
        if ((File.GetAttributes(staged) & FileAttributes.ReparsePoint) != 0 ||
            (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0))
            throw new MhxException("Updating symbolic-link executables is not supported; update their owning installation.");
        if (!Hash(staged).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new MhxException("Staged update failed SHA-256 verification.");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(staged, target, overwrite: true);
            return;
        }
        Recover(directory);
        string backup = Path.Combine(directory, ".mhx-backup-" + Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".exe" : ""));
        string journal = Path.Combine(directory, JournalName);
        bool existed = File.Exists(target);
        if (existed)
        {
            RecoveryRecord record = new() { Target = Path.GetFileName(target), Backup = Path.GetFileName(backup), OldHash = Hash(target), NewHash = expectedHash };
            string temporary = journal + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(output, record, ReleaseJsonContext.Default.RecoveryRecord);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, journal);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        bool moved = false;
        try
        {
            if (existed) { File.Move(target, backup); moved = true; }
            File.Move(staged, target);
        }
        catch
        {
            if (moved && !File.Exists(target)) File.Move(backup, target);
            if (File.Exists(target) && existed) File.Delete(journal);
            throw new MhxException("Update could not be installed. The previous executable was preserved; close running instances and retry.");
        }
        if (existed) File.Delete(journal);
        if (moved)
        {
            try { File.Delete(backup); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            // Windows can keep the renamed old image locked until its users exit.
        }
    }
}
