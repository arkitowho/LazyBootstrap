using System;
using System.ComponentModel;
using System.IO;

namespace LazyBootstrap.MediaUpdate;

// Both processes append to the same log; logging failures do not change installation results.
internal sealed class MediaUpdateLog : IDisposable
{
    private readonly StreamWriter _writer;

    public MediaUpdateLog(string game, bool append)
    {
        string directory = Path.Combine(game, MediaUpdateProtocol.UpdateStateFolderName);
        Directory.CreateDirectory(directory);
        _writer = new StreamWriter(Path.Combine(directory, MediaUpdateProtocol.UpdateLogFileName), append) { AutoFlush = true };
    }

    public void Write(string message)
    {
        try { _writer.WriteLine(message); }
        catch (IOException) { }
    }

    // Callers supply the original operation error, before adding any presentation text.
    public void Failure(string operation, Exception error, string context = null)
        => Write(FormatFailure(operation, error, context));

    internal static string FormatFailure(string operation, Exception error, string context = null)
    {
        var detail = error as MediaUpdateException;
        Exception cause = error;
        while (cause.InnerException != null) cause = cause.InnerException;
        string path = string.IsNullOrEmpty(detail?.TargetPath) ? "" : $" path={detail.TargetPath}";
        int? nativeCode = cause is Win32Exception windows ? windows.NativeErrorCode
            : ((uint)cause.HResult & 0xffff0000u) == 0x80070000u ? cause.HResult & 0xffff : null;
        string native = nativeCode.HasValue ? $" win32={nativeCode}" : "";
        return $"{operation}: {context}{path} type={cause.GetType().Name} hresult=0x{cause.HResult:X8}{native} detail={detail?.Detail ?? error.Message}";
    }

    public void Dispose()
    {
        try { _writer.Dispose(); }
        catch (IOException) { }
    }
}
