namespace Gelatinarm.Shared.Server
{
    public enum SeerrRequestOutcome
    {
        Requested,

        // Seerr already has it, or a request for it (or for every season asked for)
        AlreadyRequested,

        // The user lacks the request permission, is over quota, or the title is blocklisted
        NotAllowed,
        Failed
    }
}
