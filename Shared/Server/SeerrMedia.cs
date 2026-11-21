using System.Collections.Generic;

namespace Gelatinarm.Shared.Server
{
    /// <summary>
    ///     A film or series as Seerr knows it (from TMDB), with what Seerr has done about it.
    /// </summary>
    public class SeerrMedia
    {
        public SeerrMediaType MediaType { get; set; }
        public int TmdbId { get; set; }
        public string Title { get; set; }
        public string Year { get; set; }
        public string Overview { get; set; }
        public string PosterUrl { get; set; }
        public string BackdropUrl { get; set; }

        // Null when nobody has requested it
        public SeerrMediaStatus? Status { get; set; }

        // A series' seasons, specials left out; filled by ISeerrService.GetMediaAsync only
        public IReadOnlyList<SeerrSeason> Seasons { get; set; }

        // Null when nothing is downloading
        public SeerrDownload Download { get; set; }

        public string StatusText => DescribeStatus(Status);

        // The search tile's second line
        public string Subtitle
        {
            get
            {
                var kind = MediaType == SeerrMediaType.Tv ? "Series" : "Movie";
                var head = string.IsNullOrEmpty(Year) ? kind : $"{Year} · {kind}";
                return StatusText == null ? head : $"{head} · {StatusText}";
            }
        }

        public static string DescribeStatus(SeerrMediaStatus? status)
        {
            return status switch
            {
                SeerrMediaStatus.Pending => "Requested",
                SeerrMediaStatus.Processing => "Processing",
                SeerrMediaStatus.PartiallyAvailable => "Partly available",
                SeerrMediaStatus.Available => "Available",
                SeerrMediaStatus.Blocklisted => "Blocklisted",
                _ => null
            };
        }
    }
}
