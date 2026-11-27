using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Soltex.Customize;

/// <summary>
/// A folder only administrators and Windows can change, for the administrator helper's own state (its journal). Every
/// account can read it, so the unelevated app shows what the helper did. Nothing in it can be swapped for a link by a
/// program running without administrator rights, so the helper's writes there cannot be redirected.
/// </summary>
public static class AdministratorFolder
{
    private const int GenericAll = 0x10000000, GenericWrite = 0x40000000;
    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    /// <summary>
    /// Creates <paramref name="path"/> and each missing folder between it and <paramref name="trustedBase"/> (a folder
    /// Windows owns, such as ProgramData) so that only administrators and Windows can change them, and checks every
    /// level that already exists. Throws <see cref="UnauthorizedAccessException"/> when a level is a link or another
    /// account could change it (for example a folder created beforehand by a program without administrator rights);
    /// such a folder is refused, never repaired, because what is already inside it cannot be trusted.
    /// </summary>
    public static string Ensure(string trustedBase, string path)
    {
        if (!Path.IsPathFullyQualified(trustedBase) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("Folders must be full paths.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedBase));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The folder must be inside " + root + ".");
        string current = root;
        foreach (string part in full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            new DirectoryInfo(current).Create(Security()); // does nothing when it exists; checked either way
            Check(current);
        }
        return full;
    }

    /// <summary>
    /// The security each level is created with: Windows and administrators have full control, everyone in Users can read,
    /// nothing is inherited, and the owner gets no extra rights (a policy can make the user's own account the owner of
    /// files the helper creates).
    /// </summary>
    internal static DirectorySecurity Security()
    {
        const InheritanceFlags Everything = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        const FileSystemRights Read = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
        DirectorySecurity security = new();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, Everything, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, Everything, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, Read, Everything, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(OwnerRights, Read, Everything, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>Throws when the folder is missing, a link, or can be changed by an account other than administrators and Windows.</summary>
    internal static void Check(string path)
    {
        DirectoryInfo folder = new(path);
        if (!folder.Exists || folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException(path + " is missing or redirected, so the administrator helper will not use it.");
        if (!IsAdministratorsOnly(folder.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)))
            throw new UnauthorizedAccessException(path + " can be changed by accounts other than administrators, so the administrator helper will not use it. Delete the folder and try again.");
    }

    /// <summary>True when administrators or Windows own the folder and no one else may change it or what is created in it.</summary>
    internal static bool IsAdministratorsOnly(DirectorySecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted(owner)) return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            // Inherit-only entries (such as CREATOR OWNER) can carry generic rights; they count too.
            bool writes = (rule.FileSystemRights & WriteRights) != 0 || ((int)rule.FileSystemRights & (GenericAll | GenericWrite)) != 0;
            if (writes && (rule.IdentityReference is not SecurityIdentifier who || !Trusted(who))) return false;
        }
        return true;
    }

    private static bool Trusted(SecurityIdentifier sid) => sid == Administrators || sid == LocalSystem || sid == TrustedInstaller;
}

/// <summary>
/// File access for a process with more rights than the folder's owner (the administrator helper using files in the
/// user's own folder). The root must be a real folder (not a link). Every folder below it, and then the file, is opened
/// relative to the folder handle above it without following links, and each must be a real folder or a plain file (not
/// a symbolic link, junction or, for reading, a second hard link). Because nothing is looked up by path again after it
/// was checked, swapping a folder for a link at any moment cannot send a read or write anywhere else; and Windows'
/// own redirection of a whole folder (for example for a packaged parent app) keeps working.
/// </summary>
public static partial class VerifiedFile
{
    private const uint ListDirectory = 0x1, Traverse = 0x20, ReadAttributes = 0x80, DeleteAccess = 0x10000, Synchronize = 0x100000;
    private const uint GenericRead = 0x80000000, GenericWriteAccess = 0x40000000;
    private const uint OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePointFlag = 0x00200000;
    private const uint FileOpen = 1, FileCreate = 2;
    private const uint DirectoryFile = 0x1, WriteThrough = 0x2, SynchronousIo = 0x20, NonDirectoryFile = 0x40, OpenReparsePoint = 0x200000;
    private const uint CaseInsensitive = 0x40, AttributeNormal = 0x80, DirectoryAttribute = 0x10, ReparsePointAttribute = 0x400;
    private const int FileDispositionInfo = 4;
    private const FileShare AnyShare = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>Reads a plain file at exactly <paramref name="path"/> inside <paramref name="root"/>.</summary>
    public static string ReadAllText(string root, string path)
    {
        using StreamReader reader = OpenText(root, path);
        return reader.ReadToEnd();
    }

    /// <summary>Opens a plain file at exactly <paramref name="path"/> inside <paramref name="root"/> for reading.</summary>
    public static StreamReader OpenText(string root, string path)
    {
        using Folder folder = Folder.Open(root, path, out string name);
        SafeFileHandle handle = folder.OpenFile(name, GenericRead | Synchronize, FileShare.Read, FileOpen, 0);
        try
        {
            if ((Information(handle, out uint links) & (DirectoryAttribute | ReparsePointAttribute)) != 0 || links != 1)
                throw new UnauthorizedAccessException(name + " is a link, so it was not read.");
            return new StreamReader(new FileStream(handle, FileAccess.Read), Encoding.UTF8);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes <paramref name="contents"/> (UTF-8) as a new file at exactly <paramref name="path"/> inside
    /// <paramref name="root"/>. Whatever was at that name (a file or a link) is removed first; a link is removed itself,
    /// never what it points to.
    /// </summary>
    public static void WriteNew(string root, string path, string contents)
    {
        using Folder folder = Folder.Open(root, path, out string name);
        SafeFileHandle existing;
        try { existing = folder.OpenFile(name, DeleteAccess | ReadAttributes | Synchronize, AnyShare, FileOpen, 0); }
        catch (FileNotFoundException) { existing = new SafeFileHandle(); }
        using (existing)
            if (!existing.IsInvalid) Discard(existing);
        using SafeFileHandle handle = folder.OpenFile(name, GenericWriteAccess | Synchronize, FileShare.None, FileCreate, WriteThrough);
        using FileStream stream = new(handle, FileAccess.Write);
        stream.Write(Encoding.UTF8.GetBytes(contents));
        stream.Flush(flushToDisk: true);
    }

    /// <summary>The checked folder that holds the file: the root, then each folder below it opened relative to the one above.</summary>
    private sealed class Folder : IDisposable
    {
        private SafeFileHandle handle;

        private Folder(SafeFileHandle handle) => this.handle = handle;

        public static Folder Open(string root, string path, out string name)
        {
            string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(Path.GetFileName(full) + " is outside " + rootFull + ".");
            string[] parts = full[(rootFull.Length + 1)..].Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => p.Length == 0)) throw new ArgumentException("The path " + full + " is not a file inside " + rootFull + ".");
            name = parts[^1];
            SafeFileHandle top = CreateFile(rootFull, Traverse | ListDirectory | ReadAttributes, (uint)AnyShare, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePointFlag, IntPtr.Zero);
            if (top.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                top.Dispose();
                throw error is 2 or 3 ? new DirectoryNotFoundException(rootFull + " was not found.") : new IOException(rootFull + ": " + Marshal.GetPInvokeErrorMessage(error));
            }
            Folder folder = new(top);
            try
            {
                folder.RequireRealFolder(rootFull);
                foreach (string part in parts[..^1])
                {
                    SafeFileHandle next = folder.OpenRelative(part, Traverse | ListDirectory | ReadAttributes | Synchronize, AnyShare, FileOpen, DirectoryFile);
                    folder.handle.Dispose();
                    folder.handle = next;
                    folder.RequireRealFolder(part);
                }
                return folder;
            }
            catch
            {
                folder.Dispose();
                throw;
            }
        }

        /// <summary>Opens a file in this folder without following a link at its name.</summary>
        public SafeFileHandle OpenFile(string name, uint access, FileShare share, uint disposition, uint options) =>
            OpenRelative(name, access, share, disposition, NonDirectoryFile | options);

        private void RequireRealFolder(string name)
        {
            if ((Information(handle, out _) & (DirectoryAttribute | ReparsePointAttribute)) != DirectoryAttribute)
                throw new UnauthorizedAccessException(name + " is redirected, so the administrator helper will not use it.");
        }

        private unsafe SafeFileHandle OpenRelative(string name, uint access, FileShare share, uint disposition, uint options)
        {
            bool added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                fixed (char* text = name)
                {
                    UnicodeString unicode = new() { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = text };
                    ObjectAttributes attributes = new()
                    {
                        Length = (uint)sizeof(ObjectAttributes), RootDirectory = handle.DangerousGetHandle(), ObjectName = &unicode, Attributes = CaseInsensitive
                    };
                    IoStatusBlock status;
                    int result = NtCreateFile(out SafeFileHandle opened, access, &attributes, &status, null, AttributeNormal, (uint)share, disposition,
                        options | SynchronousIo | OpenReparsePoint, null, 0);
                    if (result >= 0) return opened;
                    opened.Dispose();
                    throw Failure(result, name);
                }
            }
            finally
            {
                if (added) handle.DangerousRelease();
            }
        }

        public void Dispose() => handle.Dispose();
    }

    private static Exception Failure(int status, string name) => unchecked((uint)status) switch
    {
        0xC0000034 => new FileNotFoundException(name + " was not found.", name),
        0xC000003A => new DirectoryNotFoundException("The folder of " + name + " was not found."),
        0xC0000022 => new UnauthorizedAccessException("Access to " + name + " was denied."),
        0xC0000035 => new IOException(name + " already exists."),
        0xC0000043 => new IOException(name + " is in use."),
        _ => new IOException(name + ": " + Marshal.GetPInvokeErrorMessage(RtlNtStatusToDosError(status)))
    };

    /// <summary>The file's attributes and its number of hard links.</summary>
    private static unsafe uint Information(SafeFileHandle handle, out uint links)
    {
        // BY_HANDLE_FILE_INFORMATION: attributes first, number of links at the 11th DWORD.
        uint* information = stackalloc uint[13];
        if (!GetFileInformationByHandle(handle, information)) throw new IOException(Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError()));
        links = information[10];
        return information[0];
    }

    private static unsafe void Discard(SafeFileHandle handle)
    {
        byte delete = 1; // FILE_DISPOSITION_INFO { BOOLEAN DeleteFile }
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, &delete, 1))
            throw new IOException(Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtCreateFile(out SafeFileHandle handle, uint access, ObjectAttributes* attributes, IoStatusBlock* status, long* allocationSize,
        uint fileAttributes, uint share, uint disposition, uint options, void* extendedAttributes, uint extendedAttributesLength);

    [LibraryImport("ntdll.dll")]
    private static partial int RtlNtStatusToDosError(int status);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandle(SafeFileHandle file, uint* information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte* information, uint size);
}
