using System.Collections.Immutable;

namespace DatabaseEditor.Modules.V1;

public sealed record BlobPutResult(Result Result, string? Sha256);
public sealed record BlobGetResult(Result Result, ImmutableArray<byte> Bytes);

// Optional documents.blobs.v1 capability. IDs are SHA-256 hex, never paths.
public interface IModuleBlobStore
{
    BlobPutResult Put(ImmutableArray<byte> bytes);
    BlobGetResult Get(string sha256);
}

public interface IModuleBlobHost : IModuleHost
{
    IModuleBlobStore Blobs { get; }
}
