using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
    ///     whole, a series by season.
    /// </summary>
    public partial class RequestViewModel : BaseViewModel, IPageViewModel
    {
        private readonly ISeerrService _seerrService;
        private SeerrMedia _media;

        [ObservableProperty] private ImageSource _backdropImage;
        [ObservableProperty] private ImageSource _posterImage;
        [ObservableProperty] private string _title;
        [ObservableProperty] private string _subtitle;
        [ObservableProperty] private string _overview;
        [ObservableProperty] private string _statusText;
        [ObservableProperty] private string _message;
        [ObservableProperty] private bool _isSeries;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RequestCommand))]
        [NotifyCanExecuteChangedFor(nameof(SelectAllSeasonsCommand))]
        private bool _canRequest;

        public RequestViewModel(ISeerrService seerrService, ILogger<RequestViewModel> logger) : base(logger)
        {
            _seerrService = seerrService;
        }

        public ObservableCollection<SeerrSeason> Seasons { get; } = new();

        // Reached from the search page alone, which passes the result it was opened from
        public void Initialize(object parameter)
        {
            _media = (SeerrMedia)parameter;
            Show(_media);

            // The search result is a summary: the title's current state and its seasons come from Seerr
            FireAndForget(LoadAsync);
        }

        private async Task LoadAsync()
        {
            await RunOnUIThreadAsync(() => IsLoading = true);
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
                await RunOnUIThreadAsync(() => IsLoading = false);
            }
        }

        private void Show(SeerrMedia media)
        {
            Title = media.Title;
            Overview = media.Overview;
            IsSeries = media.MediaType == SeerrMediaType.Tv;

            var kind = IsSeries ? "Series" : "Movie";
            Subtitle = string.IsNullOrEmpty(media.Year) ? kind : $"{media.Year} · {kind}";
            StatusText = media.StatusText ?? "Not requested";

            PosterImage = ToImage(media.PosterUrl);
            BackdropImage = ToImage(media.BackdropUrl);

            Seasons.Clear();
            foreach (var season in media.Seasons ?? Array.Empty<SeerrSeason>())
            {
                Seasons.Add(season);
            }

            // A series is known only once its seasons have loaded; until then nothing is offered
            CanRequest = IsSeries ? Seasons.Any(s => s.IsRequestable) : media.StatusText == null;
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

            Message = null;
            IsLoading = true;
            try
            {
                var outcome = await _seerrService.RequestAsync(_media.MediaType, _media.TmdbId, seasons, DisposalCts.Token);
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
                    await LoadAsync();
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
    }
}
