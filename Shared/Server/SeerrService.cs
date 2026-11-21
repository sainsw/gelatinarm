using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;

namespace Gelatinarm.Shared.Server
{
    /// <summary>
    ///     Requests through Seerr (formerly Jellyseerr/Overseerr), found and signed in to without
    ///     anything entered on the console: the Streamyfin server plugin publishes Seerr's URL,
    ///     and Seerr's Jellyfin Quick Connect sign-in is approved with this app's own session.
    ///     Everything here is quietly empty on a server without both.
    /// </summary>
    public interface ISeerrService
    {
        /// <summary>
        ///     Films and series matching <paramref name="query" /> that the server does not have.
        /// </summary>
        Task<IReadOnlyList<SeerrMedia>> SearchAsync(string query, CancellationToken cancellationToken);

        /// <summary>
        ///     The title as Seerr has it now, with a series' seasons; null when Seerr cannot answer.
        /// </summary>
        Task<SeerrMedia> GetMediaAsync(SeerrMediaType mediaType, int tmdbId, CancellationToken cancellationToken);

        /// <summary>
        ///     Asks for a film, or for <paramref name="seasons" /> of a series.
        /// </summary>
        Task<SeerrRequestOutcome> RequestAsync(SeerrMediaType mediaType, int tmdbId, IReadOnlyList<int> seasons,
            CancellationToken cancellationToken);
    }

    public class SeerrService : BaseService, ISeerrService
    {
        private const string PosterBaseUrl = "https://image.tmdb.org/t/p/w300";
        private const string BackdropBaseUrl = "https://image.tmdb.org/t/p/w1280";

        // express-session's cookie: the only credential Seerr gives a Jellyfin sign-in
        private const string SessionCookieName = "connect.sid=";

        private readonly IAuthenticationService _authService;
        private readonly JellyfinApiClient _apiClient;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly HttpClient _seerrClient;
        private readonly SemaphoreSlim _sessionLock = new(1, 1);

        // The signed-in user's Seerr session, dropped when the user or server changes. The
        // generation stops a sign-in that was running across the change from storing its result.
        private int _generation;
        private string _seerrUrl;
        private string _sessionCookie;
        private bool _unavailable;

        public SeerrService(
            ILogger<SeerrService> logger,
            IAuthenticationService authService,
            JellyfinApiClient apiClient,
            IHttpClientFactory httpClientFactory) : base(logger)
        {
            _authService = authService;
            _apiClient = apiClient;
            _httpClientFactory = httpClientFactory;

            // The session cookie is sent by hand: the platform's cookie store is shared by the
            // whole app and would carry one user's Seerr session into the next user's requests
            _seerrClient = new HttpClient(new HttpClientHandler { UseCookies = false })
            {
                Timeout = TimeSpan.FromSeconds(SystemConstants.SeerrTimeoutSeconds)
            };
            _seerrClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            _seerrClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

            _authService.UserChanged += OnUserChanged;
        }

        public async Task<IReadOnlyList<SeerrMedia>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            try
            {
                var (status, body) = await SendAsync(HttpMethod.Get,
                    $"/api/v1/search?query={Uri.EscapeDataString(query)}&page=1", null, cancellationToken)
                    .ConfigureAwait(false);
                return status == HttpStatusCode.OK ? ReadSearchResults(body) : Array.Empty<SeerrMedia>();
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync<IReadOnlyList<SeerrMedia>>(ex,
                    CreateErrorContext("SearchAsync", ErrorCategory.Network, ErrorSeverity.Warning),
                    Array.Empty<SeerrMedia>()).ConfigureAwait(false);
            }
        }

        public async Task<SeerrMedia> GetMediaAsync(SeerrMediaType mediaType, int tmdbId,
            CancellationToken cancellationToken)
        {
            try
            {
                var (status, body) = await SendAsync(HttpMethod.Get, $"/api/v1/{ApiMediaType(mediaType)}/{tmdbId}",
                    null, cancellationToken).ConfigureAwait(false);
                if (status != HttpStatusCode.OK)
                {
                    Logger.LogWarning("Seerr has no {MediaType} {TmdbId} ({StatusCode})", mediaType, tmdbId, status);
                    return null;
                }

                using var document = JsonDocument.Parse(body);
                var media = ReadMedia(document.RootElement, mediaType);
                if (mediaType == SeerrMediaType.Tv)
                {
                    media.Seasons = ReadSeasons(document.RootElement);
                }

                return media;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync<SeerrMedia>(ex,
                    CreateErrorContext("GetMediaAsync", ErrorCategory.Network), null).ConfigureAwait(false);
            }
        }

        public async Task<SeerrRequestOutcome> RequestAsync(SeerrMediaType mediaType, int tmdbId,
            IReadOnlyList<int> seasons, CancellationToken cancellationToken)
        {
            try
            {
                var (status, _) = await SendAsync(HttpMethod.Post, "/api/v1/request",
                    RequestBody(mediaType, tmdbId, seasons), cancellationToken).ConfigureAwait(false);

                Logger.LogInformation("Seerr request for {MediaType} {TmdbId}: {StatusCode}", mediaType, tmdbId, status);
                return status switch
                {
                    HttpStatusCode.Created => SeerrRequestOutcome.Requested,

                    // 202: every season asked for is already requested or available
                    HttpStatusCode.Accepted or HttpStatusCode.Conflict => SeerrRequestOutcome.AlreadyRequested,
                    HttpStatusCode.Forbidden => SeerrRequestOutcome.NotAllowed,
                    _ => SeerrRequestOutcome.Failed
                };
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex,
                    CreateErrorContext("RequestAsync", ErrorCategory.Network), SeerrRequestOutcome.Failed)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        ///     Sends to Seerr with the user's session. A null status means there is no Seerr for this
        ///     user. Seerr answers a missing or expired session with 401 or 403, so either gets one
        ///     fresh sign-in and a retry; the second answer stands (a 403 then is a real refusal).
        /// </summary>
        private async Task<(HttpStatusCode? Status, string Body)> SendAsync(HttpMethod method, string path,
            string jsonBody, CancellationToken cancellationToken)
        {
            var result = await SendOnceAsync(method, path, jsonBody, false, cancellationToken).ConfigureAwait(false);
            if (result.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Logger.LogDebug("Seerr answered {StatusCode}; signing in again", result.Status);
                result = await SendOnceAsync(method, path, jsonBody, true, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        private async Task<(HttpStatusCode? Status, string Body)> SendOnceAsync(HttpMethod method, string path,
            string jsonBody, bool renewSession, CancellationToken cancellationToken)
        {
            var session = await GetSessionAsync(renewSession, cancellationToken).ConfigureAwait(false);
            if (session == null)
            {
                return (null, null);
            }

            using var request = new HttpRequestMessage(method, session.Value.Url + path);
            request.Headers.TryAddWithoutValidation("Cookie", session.Value.Cookie);
            if (jsonBody != null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            using var response = await _seerrClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (response.StatusCode, body);
        }

        /// <summary>
        ///     The user's Seerr URL and session, signing in first when there is none or
        ///     <paramref name="renew" /> asks for a new one. Null when this server has no Seerr for
        ///     the user; that answer is kept until the user changes. A network failure throws and
        ///     is tried again next time.
        /// </summary>
        private async Task<(string Url, string Cookie)?> GetSessionAsync(bool renew, CancellationToken cancellationToken)
        {
            await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_unavailable)
                {
                    return null;
                }

                if (!renew && _sessionCookie != null)
                {
                    return (_seerrUrl, _sessionCookie);
                }

                var generation = _generation;
                var url = _seerrUrl ?? await DiscoverSeerrUrlAsync(cancellationToken).ConfigureAwait(false);
                var cookie = url == null ? null : await SignInAsync(url, cancellationToken).ConfigureAwait(false);
                if (generation != _generation)
                {
                    return null;
                }

                _seerrUrl = url;
                _sessionCookie = cookie;
                _unavailable = cookie == null;
                return cookie == null ? null : (url, cookie);
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        // The Streamyfin plugin's client config; a server without the plugin answers 404
        private async Task<string> DiscoverSeerrUrlAsync(CancellationToken cancellationToken)
        {
            var serverUrl = _authService.ServerUrl;
            var token = _authService.AccessToken;
            if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(token))
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{serverUrl.TrimEnd('/')}/streamyfin/config");
            request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{token}\"");

            var client = _httpClientFactory.CreateClient(SystemConstants.JellyfinHttpClientName);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogInformation("No Streamyfin plugin config on the server ({StatusCode}); requests stay off",
                    response.StatusCode);
                return null;
            }

            var url = ReadSeerrUrl(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (url == null)
            {
                Logger.LogInformation("The Streamyfin plugin names no Seerr server; requests stay off");
            }

            return url;
        }

        private static string ReadSeerrUrl(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var setting = Child(Child(document.RootElement, "settings"), "jellyseerrServerUrl");
                var value = GetString(setting, "value")?.Trim();

                // An unset plugin holds a placeholder sentence here, not a URL
                return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                       (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? value.TrimEnd('/')
                    : null;
            }
            catch (JsonException)
            {
                // Something other than the plugin answered at its path
                return null;
            }
        }

        /// <summary>
        ///     Seerr's Jellyfin Quick Connect (Seerr 3.4 and later): Seerr asks the Jellyfin server
        ///     for a code, this app approves the code as the signed-in user, and Seerr exchanges it
        ///     for a session. No password is involved and no key is shared between users. Null
        ///     when Seerr is too old, Quick Connect is off, or Seerr will not let the user in.
        /// </summary>
        private async Task<string> SignInAsync(string seerrUrl, CancellationToken cancellationToken)
        {
            string code;
            string secret;
            using (var initiate = await _seerrClient.PostAsync($"{seerrUrl}/api/v1/auth/jellyfin/quickconnect/initiate",
                       null, cancellationToken).ConfigureAwait(false))
            {
                if (!initiate.IsSuccessStatusCode)
                {
                    Logger.LogInformation("Seerr did not start Quick Connect ({StatusCode}); requests stay off",
                        initiate.StatusCode);
                    return null;
                }

                using var document = JsonDocument.Parse(await initiate.Content.ReadAsStringAsync().ConfigureAwait(false));
                code = GetString(document.RootElement, "code");
                secret = GetString(document.RootElement, "secret");
            }

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(secret))
            {
                return null;
            }

            try
            {
                var authorized = await _apiClient.QuickConnect.Authorize
                    .PostAsync(config => config.QueryParameters.Code = code, cancellationToken).ConfigureAwait(false);
                if (authorized != true)
                {
                    return null;
                }
            }
            catch (ApiException ex)
            {
                Logger.LogInformation(ex, "Jellyfin refused to approve Seerr's Quick Connect code; requests stay off");
                return null;
            }

            using var content = new StringContent(SecretBody(secret), Encoding.UTF8, "application/json");
            using var response = await _seerrClient.PostAsync(
                $"{seerrUrl}/api/v1/auth/jellyfin/quickconnect/authenticate", content, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogInformation("Seerr refused the Quick Connect sign-in ({StatusCode}); requests stay off",
                    response.StatusCode);
                return null;
            }

            var cookie = ReadSessionCookie(response);
            if (cookie == null)
            {
                Logger.LogWarning("Seerr signed the user in but sent no session cookie");
            }

            return cookie;
        }

        private static string ReadSessionCookie(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
            {
                return null;
            }

            // Several cookies may arrive folded into one header, so the pair is found, not split out
            foreach (var header in headers)
            {
                var start = header.IndexOf(SessionCookieName, StringComparison.Ordinal);
                if (start >= 0)
                {
                    var end = header.IndexOf(';', start);
                    return end < 0 ? header.Substring(start) : header.Substring(start, end - start);
                }
            }

            return null;
        }

        private static IReadOnlyList<SeerrMedia> ReadSearchResults(string json)
        {
            using var document = JsonDocument.Parse(json);
            var results = new List<SeerrMedia>();
            var list = Child(document.RootElement, "results");
            if (list.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in list.EnumerateArray())
            {
                // People are in the results too
                var mediaType = GetString(item, "mediaType") switch
                {
                    "movie" => SeerrMediaType.Movie,
                    "tv" => (SeerrMediaType?)SeerrMediaType.Tv,
                    _ => null
                };

                // What the server has is in the library search already
                var mediaInfo = Child(item, "mediaInfo");
                if (mediaType == null || !string.IsNullOrEmpty(GetString(mediaInfo, "jellyfinMediaId")))
                {
                    continue;
                }

                var media = ReadMedia(item, mediaType.Value);
                if (media.Status != SeerrMediaStatus.Available)
                {
                    results.Add(media);
                }
            }

            return results;
        }

        private static SeerrMedia ReadMedia(JsonElement item, SeerrMediaType mediaType)
        {
            var isTv = mediaType == SeerrMediaType.Tv;
            var date = GetString(item, isTv ? "firstAirDate" : "releaseDate");
            var posterPath = GetString(item, "posterPath");
            var backdropPath = GetString(item, "backdropPath");

            return new SeerrMedia
            {
                MediaType = mediaType,
                TmdbId = GetInt(item, "id") ?? 0,
                Title = GetString(item, isTv ? "name" : "title"),
                Year = date?.Length >= 4 ? date.Substring(0, 4) : null,
                Overview = GetString(item, "overview"),
                PosterUrl = string.IsNullOrEmpty(posterPath) ? null : PosterBaseUrl + posterPath,
                BackdropUrl = string.IsNullOrEmpty(backdropPath) ? null : BackdropBaseUrl + backdropPath,
                Status = ToStatus(GetInt(Child(item, "mediaInfo"), "status"))
            };
        }

        /// <summary>
        ///     A series' seasons, specials left out. A season can be asked for unless the server has
        ///     it (in part or whole), it is on its way, or a request that was not declined names it.
        /// </summary>
        private static IReadOnlyList<SeerrSeason> ReadSeasons(JsonElement show)
        {
            var mediaInfo = Child(show, "mediaInfo");

            var statusBySeason = new Dictionary<int, string>();
            foreach (var season in Items(mediaInfo, "seasons"))
            {
                var number = GetInt(season, "seasonNumber");
                var text = SeerrMedia.DescribeStatus(ToStatus(GetInt(season, "status")));
                if (number.HasValue && text != null)
                {
                    statusBySeason[number.Value] = text;
                }
            }

            foreach (var request in Items(mediaInfo, "requests"))
            {
                // MediaRequestStatus.DECLINED
                if (GetInt(request, "status") == 3)
                {
                    continue;
                }

                foreach (var season in Items(request, "seasons"))
                {
                    var number = GetInt(season, "seasonNumber");
                    if (number.HasValue && !statusBySeason.ContainsKey(number.Value))
                    {
                        statusBySeason[number.Value] = "Requested";
                    }
                }
            }

            var seasons = new List<SeerrSeason>();
            foreach (var season in Items(show, "seasons"))
            {
                var number = GetInt(season, "seasonNumber");
                if (number is null or 0)
                {
                    continue;
                }

                statusBySeason.TryGetValue(number.Value, out var statusText);
                seasons.Add(new SeerrSeason
                {
                    SeasonNumber = number.Value,
                    Name = GetString(season, "name") ?? $"Season {number.Value}",
                    EpisodeCount = GetInt(season, "episodeCount") ?? 0,
                    StatusText = statusText
                });
            }

            return seasons;
        }

        private static SeerrMediaStatus? ToStatus(int? value)
        {
            return value switch
            {
                2 => SeerrMediaStatus.Pending,
                3 => SeerrMediaStatus.Processing,
                4 => SeerrMediaStatus.PartiallyAvailable,
                5 => SeerrMediaStatus.Available,
                6 => SeerrMediaStatus.Blocklisted,
                _ => null
            };
        }

        private static string RequestBody(SeerrMediaType mediaType, int tmdbId, IReadOnlyList<int> seasons)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("mediaType", ApiMediaType(mediaType));
                writer.WriteNumber("mediaId", tmdbId);
                if (mediaType == SeerrMediaType.Tv)
                {
                    writer.WriteStartArray("seasons");
                    foreach (var season in seasons)
                    {
                        writer.WriteNumberValue(season);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string SecretBody(string secret)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("secret", secret);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string ApiMediaType(SeerrMediaType mediaType)
        {
            return mediaType == SeerrMediaType.Tv ? "tv" : "movie";
        }

        // Seerr leaves out or nulls what it does not know, so every read tolerates a missing value
        private static JsonElement Child(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child)
                ? child
                : default;
        }

        private static IEnumerable<JsonElement> Items(JsonElement element, string name)
        {
            var array = Child(element, name);
            return array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : System.Array.Empty<JsonElement>();
        }

        private static string GetString(JsonElement element, string name)
        {
            var value = Child(element, name);
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static int? GetInt(JsonElement element, string name)
        {
            var value = Child(element, name);
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
        }

        private void OnUserChanged(object sender, EventArgs e)
        {
            Interlocked.Increment(ref _generation);
            _seerrUrl = null;
            _sessionCookie = null;
            _unavailable = false;
        }

        protected override void UnsubscribeEvents()
        {
            _authService.UserChanged -= OnUserChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                _seerrClient.Dispose();
                _sessionLock.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
