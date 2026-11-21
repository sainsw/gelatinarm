using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Server;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Search
{
    /// <summary>
    ///     A title the server does not have, as Seerr knows it, and the request for it: a film
    ///     whole, a series by season, in a quality profile when Seerr lets the user choose one.
    ///     While the title is processing or downloading the page asks Seerr again every few
    ///     seconds, for the progress line.
    /// </summary>
    public partial class RequestViewModel : BaseViewModel, IPageViewModel
    {
        private readonly ISeerrService _seerrService;
        private SeerrMedia _media;

        // The images shown, so a refresh does not reload (and flash) the same pictures
        private string _posterUrl;
        private string _backdropUrl;
        private DispatcherTimer _refreshTimer;

        // A refresh can outlast the interval on a slow connection; ticks meanwhile are dropped
        private bool _isRefreshing;

        [ObservableProperty] private ImageSource _backdropImage;
        [ObservableProperty] private ImageSource _posterImage;
        [ObservableProperty] private string _title;
        [ObservableProperty] private string _subtitle;
        [ObservableProperty] private string _overview;
        [ObservableProperty] private string _statusText;
        [ObservableProperty] private string _downloadText;
        [ObservableProperty] private int _downloadPercent;
        [ObservableProperty] private string _message;
        [ObservableProperty] private bool _isSeries;
        [ObservableProperty] private bool _canChooseQuality;
        [ObservableProperty] private int _selectedProfileIndex = -1;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RequestCommand))]
        [NotifyCanExecuteChangedFor(nameof(SelectAllSeasonsCommand))]
        private bool _canRequest;

        public RequestViewModel(ISeerrService seerrService, ILogger<RequestViewModel> logger) : base(logger)
        {
            _seerrService = seerrService;
        }

        public ObservableCollection<SeerrSeason> Seasons { get; } = new();

        public ObservableCollection<SeerrQualityProfile> Profiles { get; } = new();

        // Reached from the search page alone, which passes the result it was opened from
        public void Initialize(object parameter)
        {
            _media = (SeerrMedia)parameter;
            Show(_media);

            // The search result is a summary: the title's current state and its seasons come from Seerr
            FireAndForget(() => LoadAsync(true));
            FireAndForget(LoadRequestOptionsAsync);
        }

        private async Task LoadAsync(bool showOverlay)
        {
            if (showOverlay)
            {
                await RunOnUIThreadAsync(() => IsLoading = true);
            }

            try
            {
                var media = await _seerrService.GetMediaAsync(_media.MediaType, _media.TmdbId, DisposalCts.Token)
                    .ConfigureAwait(false);
                await RunOnUIThreadAsync(() =>
                {
                    if (media == null)
                    {
                        CanRequest = false;
                        Message = "Seerr could not be reached. Try again later.";
                        return;
                    }

                    _media = media;
                    Show(media);
                });
            }
            finally
            {
                if (showOverlay)
                {
                    await RunOnUIThreadAsync(() => IsLoading = false);
                }
            }
        }

        private async Task LoadRequestOptionsAsync()
        {
            var options = await _seerrService.GetRequestOptionsAsync(_media.MediaType, DisposalCts.Token)
                .ConfigureAwait(false);
            if (options == null)
            {
                return;
            }

            await RunOnUIThreadAsync(() =>
            {
                foreach (var profile in options.Profiles)
                {
                    Profiles.Add(profile);
                }

                SelectedProfileIndex = options.DefaultProfileIndex;
                UpdateCanChooseQuality();
            });
        }

        private void Show(SeerrMedia media)
        {
            Title = media.Title;
            Overview = media.Overview;
            IsSeries = media.MediaType == SeerrMediaType.Tv;

            var kind = IsSeries ? "Series" : "Movie";
            Subtitle = string.IsNullOrEmpty(media.Year) ? kind : $"{media.Year} · {kind}";
            StatusText = media.StatusText ?? "Not requested";
            DownloadText = media.Download?.Text;
            DownloadPercent = media.Download?.Percent ?? 0;

            if (media.PosterUrl != _posterUrl)
            {
                _posterUrl = media.PosterUrl;
                PosterImage = ToImage(_posterUrl);
            }

            if (media.BackdropUrl != _backdropUrl)
            {
                _backdropUrl = media.BackdropUrl;
                BackdropImage = ToImage(_backdropUrl);
            }

            // A refresh rebuilds the seasons; the user's ticks survive it where the season still can be asked for
            var ticked = new HashSet<int>(Seasons.Where(s => s.IsSelected).Select(s => s.SeasonNumber));
            Seasons.Clear();
            foreach (var season in media.Seasons ?? Array.Empty<SeerrSeason>())
            {
                season.IsSelected = season.IsRequestable && ticked.Contains(season.SeasonNumber);
                Seasons.Add(season);
            }

            // A series is known only once its seasons have loaded; until then nothing is offered
            CanRequest = IsSeries ? Seasons.Any(s => s.IsRequestable) : media.StatusText == null;
            UpdateCanChooseQuality();
            UpdateRefreshTimer(media);
        }

        private void UpdateCanChooseQuality()
        {
            CanChooseQuality = CanRequest && Profiles.Count > 0;
        }

        private void UpdateRefreshTimer(SeerrMedia media)
        {
            var inProgress = media.Download != null || media.Status == SeerrMediaStatus.Processing;
            if (inProgress && _refreshTimer == null)
            {
                _refreshTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(SearchConstants.RequestProgressRefreshSeconds)
                };
                _refreshTimer.Tick += OnRefreshTick;
                _refreshTimer.Start();
            }
            else if (!inProgress)
            {
                StopRefreshTimer();
            }
        }

        private void StopRefreshTimer()
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Tick -= OnRefreshTick;
                _refreshTimer = null;
            }
        }

        private void OnRefreshTick(object sender, object e)
        {
            if (_isRefreshing)
            {
                return;
            }

            _isRefreshing = true;
            FireAndForget(async () =>
            {
                try
                {
                    await LoadAsync(false);
                }
                finally
                {
                    _isRefreshing = false;
                }
            });
        }

        private static ImageSource ToImage(string url)
        {
            return string.IsNullOrEmpty(url) ? null : new BitmapImage(new Uri(url));
        }

        // Read through a method: the generated commands ask it, and the analyzers do not count
        // a nameof as a read
        private bool HasSomethingToRequest()
        {
            return CanRequest;
        }

        [RelayCommand(CanExecute = nameof(HasSomethingToRequest))]
        private void SelectAllSeasons()
        {
            foreach (var season in Seasons.Where(s => s.IsRequestable))
            {
                season.IsSelected = true;
            }
        }

        [RelayCommand(CanExecute = nameof(HasSomethingToRequest))]
        private async Task RequestAsync()
        {
            var seasons = Seasons.Where(s => s.IsSelected && s.IsRequestable).Select(s => s.SeasonNumber).ToArray();
            if (IsSeries && seasons.Length == 0)
            {
                Message = "Choose the seasons to request first.";
                return;
            }

            var profile = CanChooseQuality && SelectedProfileIndex >= 0 && SelectedProfileIndex < Profiles.Count
                ? Profiles[SelectedProfileIndex]
                : null;

            Message = null;
            IsLoading = true;
            try
            {
                var outcome = await _seerrService.RequestAsync(_media.MediaType, _media.TmdbId, seasons, profile,
                    DisposalCts.Token);
                Message = outcome switch
                {
                    SeerrRequestOutcome.Requested => "Requested.",
                    SeerrRequestOutcome.AlreadyRequested => "This has already been requested.",
                    SeerrRequestOutcome.NotAllowed =>
                        "Seerr turned the request down. Your account may not be allowed to request this, or may be over its limit.",
                    _ => "The request did not go through. Try again later."
                };

                if (outcome is SeerrRequestOutcome.Requested or SeerrRequestOutcome.AlreadyRequested)
                {
                    await LoadAsync(true);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("RequestAsync", ErrorCategory.Network), false);
                Message = "The request did not go through. Try again later.";
            }
            finally
            {
                IsLoading = false;
            }
        }

        protected override void DisposeManaged()
        {
            base.DisposeManaged();
            StopRefreshTimer();
        }
    }
}
