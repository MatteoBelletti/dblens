namespace DbLens.Core.Analysis.Gdpr;

public sealed record TableGap(
    bool HasSoftDelete,
    bool HasCreatedAt,
    bool HasUpdatedAt,
    bool HasEncryptionMarker);
