using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LazyBootstrap.MediaUpdate;

internal static class MediaUpdateAccess
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint Delete = 0x00010000;
    private const uint AddFile = 0x0002;
    private const uint AddDirectory = 0x0004;

    public static void Read(string path) => Open(path, GenericRead, FileShare.Read, false);

    public static void Overwrite(string path)
    {
        RejectReadOnly(path);
        // CopyFile and create/truncate writes reject existing hidden targets even when OPEN_EXISTING succeeds.
        if ((File.GetAttributes(path) & FileAttributes.Hidden) != 0)
            throw new MediaUpdateException(path, "目标文件为隐藏文件，无法直接覆盖。", null);
        Open(path, GenericWrite, FileShare.None, false);
    }

    public static void Remove(string path, bool directory)
    {
        if (!directory) RejectReadOnly(path);
        Open(path, Delete, FileShare.None, directory);
    }

    public static void CreateIn(string parent, bool directory, bool newParents)
        => Open(parent, (directory ? AddDirectory : AddFile) | (newParents ? AddDirectory : 0),
            FileShare.ReadWrite | FileShare.Delete, true);

    private static void RejectReadOnly(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new MediaUpdateException(path, "文件为只读，无法覆盖或删除。", null);
    }

    private static void Open(string path, uint access, FileShare share, bool directory)
    {
        // OPEN_EXISTING never creates/truncates a file. Handles are released before installation.
        using var handle = CreateFileW(path, access, (uint)share, IntPtr.Zero, 3,
            directory ? 0x02000000u : 0u, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            throw new MediaUpdateException(path,
                $"文件被占用或权限不足（Windows 错误 {error.NativeErrorCode}：{error.Message}）。", error);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
}
