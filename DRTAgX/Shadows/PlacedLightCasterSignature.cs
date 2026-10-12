using System;

namespace DRTAgX;

// Flags identify unsupported native mesh layouts for safe cache fallback.
[Flags]
internal enum CasterSignatureReason
{
    None = 0,
    NullPart = 1,
    VertexCount = 2,
    InstancedGeometry = 4,
    Positions = 8,
    Uvs = 16,
    Flags = 32,
    Indices = 64,
    OptionalStream = 128,
    TextureRouting = 256,
    CustomStream = 512,
    CaptureException = 1024,
    MissingChunk = 2048
}

