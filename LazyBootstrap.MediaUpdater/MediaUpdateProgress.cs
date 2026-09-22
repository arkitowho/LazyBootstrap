using System;

namespace LazyBootstrap.MediaUpdate;

internal enum MediaUpdateStage { Waiting, Preflight, Installing, Cleanup, Completed }
internal enum MediaUpdateStatus { Running, Succeeded, Failed, Cancelled }

// A complete immutable snapshot: the renderer can skip intermediate frames safely.
internal sealed record MediaUpdateProgress(MediaUpdateStage Stage, string Message)
{
    public MediaUpdateStatus Status { get; init; }
    public MediaUpdateAction? Operation { get; init; }
    public string Path { get; init; }
    public string Detail { get; init; }
    public int Completed { get; init; }
    public int Total { get; init; }
    public int? RemainingSeconds { get; init; }
    public string Warning { get; init; }
    public bool RequiresAcknowledgement { get; init; }

    public static void Send(Action<MediaUpdateProgress> progress, MediaUpdateProgress value)
    {
        // Presentation must never change the outcome of a file operation.
        try { progress?.Invoke(value); } catch { }
    }
}
