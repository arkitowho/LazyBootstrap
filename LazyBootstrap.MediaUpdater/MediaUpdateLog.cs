using System;
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

    public void Dispose()
    {
        try { _writer.Dispose(); }
        catch (IOException) { }
    }
}
