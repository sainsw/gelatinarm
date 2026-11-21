using System;

namespace Gelatinarm.Shared.Server
{
    /// <summary>
    ///     A title's downloads in progress, as Radarr or Sonarr report them to Seerr: one file for a
    ///     film, one per episode for a series.
    /// </summary>
    public class SeerrDownload
    {
        public int Percent { get; set; }

        // The slowest file's estimate; null while the downloader has none (queued, stalled)
        public TimeSpan? TimeLeft { get; set; }

        public int EpisodeCount { get; set; }

        public string Text
        {
            get
            {
                var what = EpisodeCount > 1 ? $"Downloading {EpisodeCount} episodes" : "Downloading";
                var text = $"{what} · {Percent}%";
                return TimeLeft.HasValue ? $"{text} · {FormatTimeLeft(TimeLeft.Value)} left" : text;
            }
        }

        private static string FormatTimeLeft(TimeSpan timeLeft)
        {
            if (timeLeft < TimeSpan.FromMinutes(1))
            {
                return "under a minute";
            }

            var rounded = TimeSpan.FromMinutes(Math.Round(timeLeft.TotalMinutes));
            return rounded < TimeSpan.FromHours(1)
                ? $"{rounded.Minutes} min"
                : $"{(int)rounded.TotalHours} h {rounded.Minutes} min";
        }
    }
}
