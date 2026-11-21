namespace Gelatinarm.Shared.Server
{
    // What Seerr has done about a title or season (its MediaStatus, server/constants/media.ts),
    // less UNKNOWN and DELETED, which read as nothing done
    public enum SeerrMediaStatus
    {
        Pending,
        Processing,
        PartiallyAvailable,
        Available,
        Blocklisted
    }
}
