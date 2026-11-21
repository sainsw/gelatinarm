using CommunityToolkit.Mvvm.ComponentModel;

namespace Gelatinarm.Shared.Server
{
    /// <summary>
    ///     One season of a series in Seerr, and whether it can still be asked for.
    /// </summary>
    public partial class SeerrSeason : ObservableObject
    {
        [ObservableProperty] private bool _isSelected;

        public int SeasonNumber { get; set; }
        public string Name { get; set; }
        public int EpisodeCount { get; set; }

        // Null when nobody has asked for it and the server does not have it
        public string StatusText { get; set; }

        public bool IsRequestable => StatusText == null;

        public string Details
        {
            get
            {
                var episodes = EpisodeCount == 1 ? "1 episode" : $"{EpisodeCount} episodes";
                return StatusText == null ? episodes : $"{episodes} · {StatusText}";
            }
        }
    }
}
