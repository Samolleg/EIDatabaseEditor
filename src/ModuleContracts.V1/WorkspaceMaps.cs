using System.Collections.Immutable;

namespace DatabaseEditor.Modules.V1;

public sealed record WorkspaceMapsResult(Result Result, WorkspaceStamp Stamp, ImmutableArray<string> Paths);
public sealed record WorkspaceCacheResult(Result Result, ImmutableArray<byte> Bytes);

// Optional workspace.maps-cache.v1. Expected SessionId/Generation guard every operation;
// Revision does not invalidate a per-database cache. Payload schema belongs to the module.
public interface IWorkspaceMapsCache
{
    Task<WorkspaceMapsResult> DiscoverAsync(WorkspaceStamp expected, CancellationToken cancellationToken = default);
    Task<WorkspaceCacheResult> LoadAsync(WorkspaceStamp expected, CancellationToken cancellationToken = default);
    Task<Result> SaveAsync(WorkspaceStamp expected, ImmutableArray<byte> bytes, CancellationToken cancellationToken = default);
}
public interface IModuleMapsCacheHost : IModuleHost
{
    IWorkspaceMapsCache MapsCache { get; }
}
