using System.Collections.Generic;

namespace Gelatinarm.Shared.Server
{
    /// <summary>
    ///     The quality profiles a user may choose between, offered only to users Seerr lets pick
    ///     them (Request Advanced, Manage Requests or Admin), as Seerr's own request dialog does.
    /// </summary>
    public class SeerrRequestOptions
    {
        public IReadOnlyList<SeerrQualityProfile> Profiles { get; set; }

        // The server's own default, chosen until the user picks another
        public int DefaultProfileIndex { get; set; }
    }
}
