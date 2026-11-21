using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Seerr.Tests.Fakes;
using Gelatinarm.Shared.Server;
using Xunit;

namespace Gelatinarm.Seerr.Tests
{
    // What the service makes of Seerr's search, title and request answers
    public class SeerrDataTests
    {
        private const string SearchResults = @"{""page"":1,""results"":[
            {""mediaType"":""tv"",""id"":2567,""name"":""Hercules"",""firstAirDate"":""1998-08-31"",
             ""posterPath"":""/poster.jpg"",""backdropPath"":""/backdrop.jpg"",""overview"":""Labours."",""mediaInfo"":null},
            {""mediaType"":""movie"",""id"":11970,""title"":""Hercules"",""releaseDate"":""1997-06-13"",
             ""mediaInfo"":{""status"":5,""jellyfinMediaId"":""e9f1dffc""}},
            {""mediaType"":""movie"",""id"":184315,""title"":""Hercules"",""releaseDate"":""2014-07-23"",
             ""mediaInfo"":{""status"":2,""jellyfinMediaId"":null}},
            {""mediaType"":""movie"",""id"":500,""title"":""Available elsewhere"",""mediaInfo"":{""status"":5}},
            {""mediaType"":""person"",""id"":7,""name"":""Kevin Sorbo""},
            {""mediaType"":""movie"",""id"":600,""title"":""Undated""}
        ]}";

        [Fact]
        public async Task Search_keeps_films_and_series_the_server_does_not_have()
        {
            await using var server = await FakeServer.StartAsync();
            server.SearchJson = SearchResults;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var results = await service.SearchAsync("hercules", CancellationToken.None);

            Assert.Equal(new[] { 2567, 184315, 600 }, results.Select(r => r.TmdbId));
        }

        [Fact]
        public async Task Search_results_carry_what_the_tiles_show()
        {
            await using var server = await FakeServer.StartAsync();
            server.SearchJson = SearchResults;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var results = await service.SearchAsync("hercules", CancellationToken.None);

            var series = results[0];
            Assert.Equal(SeerrMediaType.Tv, series.MediaType);
            Assert.Equal("Hercules", series.Title);
            Assert.Equal("1998", series.Year);
            Assert.Null(series.Status);
            Assert.Equal("1998 · Series", series.Subtitle);
            Assert.Equal("https://image.tmdb.org/t/p/w300/poster.jpg", series.PosterUrl);
            Assert.Equal("https://image.tmdb.org/t/p/w1280/backdrop.jpg", series.BackdropUrl);

            Assert.Equal("2014 · Movie · Requested", results[1].Subtitle);
            Assert.Equal("Movie", results[2].Subtitle);
            Assert.Null(results[2].PosterUrl);
        }

        [Theory]
        [InlineData("it's (2019)", "it%27s%20%282019%29")]
        [InlineData("amélie & co", "am%C3%A9lie%20%26%20co")]
        public async Task Search_escapes_the_query(string query, string expected)
        {
            await using var server = await FakeServer.StartAsync();
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            await service.SearchAsync(query, CancellationToken.None);

            Assert.Equal($"?query={expected}&page=1", server.LastSearchQueryString);
        }

        [Fact]
        public async Task Malformed_search_answers_come_back_empty()
        {
            await using var server = await FakeServer.StartAsync();
            server.SearchJson = "<html>proxy error</html>";
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            Assert.Empty(await service.SearchAsync("x", CancellationToken.None));
            Assert.Single(service.Errors.Errors);
        }

        [Fact]
        public async Task A_series_lists_its_seasons_and_which_can_be_asked_for()
        {
            await using var server = await FakeServer.StartAsync();
            server.MediaJson["tv/1399"] = @"{""id"":1399,""name"":""Thrones"",""firstAirDate"":""2011-04-17"",
                ""seasons"":[
                    {""seasonNumber"":0,""name"":""Specials"",""episodeCount"":10},
                    {""seasonNumber"":1,""name"":""Season 1"",""episodeCount"":10},
                    {""seasonNumber"":2,""name"":""Season 2"",""episodeCount"":10},
                    {""seasonNumber"":3,""name"":""Season 3"",""episodeCount"":1},
                    {""seasonNumber"":4,""episodeCount"":10},
                    {""seasonNumber"":5,""name"":""Season 5"",""episodeCount"":10}],
                ""mediaInfo"":{""status"":4,
                    ""seasons"":[{""seasonNumber"":1,""status"":5},{""seasonNumber"":2,""status"":4},{""seasonNumber"":5,""status"":1}],
                    ""requests"":[
                        {""status"":2,""seasons"":[{""seasonNumber"":3},{""seasonNumber"":2}]},
                        {""status"":3,""seasons"":[{""seasonNumber"":4}]}]}}";
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var show = await service.GetMediaAsync(SeerrMediaType.Tv, 1399, CancellationToken.None);

            Assert.Equal("Partly available", show.StatusText);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, show.Seasons.Select(s => s.SeasonNumber));
            Assert.Equal(new[] { "Available", "Partly available", "Requested", null, null },
                show.Seasons.Select(s => s.StatusText));
            Assert.Equal(new[] { false, false, false, true, true }, show.Seasons.Select(s => s.IsRequestable));
            Assert.Equal("Season 4", show.Seasons[3].Name);
            Assert.Equal("1 episode · Requested", show.Seasons[2].Details);
            Assert.Equal("10 episodes", show.Seasons[4].Details);
        }

        [Fact]
        public async Task A_film_has_no_seasons()
        {
            await using var server = await FakeServer.StartAsync();
            server.MediaJson["movie/11"] = @"{""id"":11,""title"":""Star Wars"",""releaseDate"":""1977-05-25"",""mediaInfo"":{""status"":3}}";
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var film = await service.GetMediaAsync(SeerrMediaType.Movie, 11, CancellationToken.None);

            Assert.Equal("Processing", film.StatusText);
            Assert.Null(film.Seasons);
        }

        [Fact]
        public async Task A_title_seerr_does_not_know_is_null()
        {
            await using var server = await FakeServer.StartAsync();
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            Assert.Null(await service.GetMediaAsync(SeerrMediaType.Movie, 404, CancellationToken.None));
        }

        [Fact]
        public async Task A_film_request_names_the_film_only()
        {
            await using var server = await FakeServer.StartAsync();
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var outcome = await service.RequestAsync(SeerrMediaType.Movie, 11, Array.Empty<int>(), null, CancellationToken.None);

            Assert.Equal(SeerrRequestOutcome.Requested, outcome);
            Assert.Equal("{\"mediaType\":\"movie\",\"mediaId\":11}", server.LastRequestBody);
        }

        [Fact]
        public async Task A_series_request_names_its_seasons()
        {
            await using var server = await FakeServer.StartAsync();
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            await service.RequestAsync(SeerrMediaType.Tv, 1399, new[] { 4, 5 }, null, CancellationToken.None);

            Assert.Equal("{\"mediaType\":\"tv\",\"mediaId\":1399,\"seasons\":[4,5]}", server.LastRequestBody);
        }

        [Theory]
        [InlineData(201, SeerrRequestOutcome.Requested)]
        [InlineData(202, SeerrRequestOutcome.AlreadyRequested)]
        [InlineData(409, SeerrRequestOutcome.AlreadyRequested)]
        [InlineData(500, SeerrRequestOutcome.Failed)]
        public async Task Request_answers_map_to_outcomes(int status, SeerrRequestOutcome expected)
        {
            await using var server = await FakeServer.StartAsync();
            server.RequestStatuses.Enqueue(status);
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            Assert.Equal(expected,
                await service.RequestAsync(SeerrMediaType.Movie, 11, Array.Empty<int>(), null, CancellationToken.None));
        }

        [Fact]
        public async Task A_refusal_that_survives_a_fresh_sign_in_is_not_allowed()
        {
            await using var server = await FakeServer.StartAsync();
            server.RequestStatuses.Enqueue(403);
            server.RequestStatuses.Enqueue(403);
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var outcome = await service.RequestAsync(SeerrMediaType.Movie, 11, Array.Empty<int>(), null, CancellationToken.None);

            Assert.Equal(SeerrRequestOutcome.NotAllowed, outcome);
            Assert.Equal(2, server.InitiateCalls);
        }

        [Fact]
        public async Task Without_seerr_a_request_fails_quietly()
        {
            await using var server = await FakeServer.StartAsync();
            server.ConfigStatus = 404;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var outcome = await service.RequestAsync(SeerrMediaType.Movie, 11, Array.Empty<int>(), null, CancellationToken.None);

            Assert.Equal(SeerrRequestOutcome.Failed, outcome);
            Assert.Null(server.LastRequestBody);
        }
    }
}
