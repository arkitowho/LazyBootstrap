using System;
using System.ComponentModel;
using System.IO;

namespace LazyBootstrap.MediaUpdate;

// Preserve a complete text error while exposing file and cause separately to the dashboard.
internal sealed class MediaUpdateException : IOException
{
    public string TargetPath { get; }
    public string Detail { get; }

    public MediaUpdateException(string path, string detail, Exception inner, string message = null)
        : base(message ?? $"无法安装 {path}：{detail}", inner)
    {
        TargetPath = path;
        Detail = detail;
    }

    // Standard filesystem exceptions embed a path in Message; use their native error code
    // for a path-free cause while preserving the complete message in logs.
    internal static string GetDetail(Exception error)
    {
        if (error is MediaUpdateException update) return update.Detail;
        if ((error is IOException or UnauthorizedAccessException) && ((uint)error.HResult & 0xffff0000u) == 0x80070000u)
        {
            int code = error.HResult & 0xffff;
            return $"Windows 错误 {code}：{new Win32Exception(code).Message}";
        }
        return error.Message;
    }
}
