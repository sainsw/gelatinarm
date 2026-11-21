using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Seerr.Tests.Fakes;
using Gelatinarm.Shared.Server;
using Xunit;

namespace Gelatinarm.Seerr.Tests
{
    // The quality profile choice offered to users Seerr allows it, and the download progress line
    public class SeerrQualityAndProgressTests
    {
        private const string RadarrServers =
            @"[{""id"":0,""name"":""Radarr"",""is4k"":false,""isDefault"":true,""activeProfileId"":5}]";

        private const string RadarrDetail = @"{""server"":{""id"":0,""activeProfileId"":5},
            ""profiles"":[{""id"":1,""name"":""Any""},{""id"":4,""name"":""HD-1080p""},{""id"":5,""name"":""Ultra-HD""}],
            ""rootFolders"":[{""id"":1,""path"":""/movies""}]}";

        private static async Task<FakeServer> ServerWithRadarrAsync(int permissions)
        {
            var server = await FakeServer.StartAsync();
            server.Permissions = permissions;
            server.ServiceJson["radarr"] = RadarrServers;
            server.ServiceJson["radarr/0"] = RadarrDetail;
            return server;
        }

        [Theory]
        [InlineData(2)] // Admin
        [InlineData(16)] // Manage Requests
        [InlineData(8192 | 32)] // Request Advanced
        [InlineData(9445536)] // a real user's flags, Request Advanced among them
        public async Task Users_seerr_lets_choose_get_the_profiles(int permissions)
        {
            await using var server = await ServerWithRadarrAsync(permissions);
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var options = await service.GetRequestOptionsAsync(SeerrMediaType.Movie, CancellationToken.None);

            Assert.Equal(new[] { "Any", "HD-1080p", "Ultra-HD" }, options.Profiles.Select(p => p.Name));
            Assert.Equal(new[] { 1, 4, 5 }, options.Profiles.Select(p => p.Id));
            Assert.All(options.Profiles, p => Assert.Equal(0, p.ServerId));
            Assert.Equal(2, options.DefaultProfileIndex);
        }

        [Theory]
        [InlineData(32)] // Request
        [InlineData(79708192)] // a real user's flags, without Request Advanced
        [InlineData(0)]
        public async Task Other_users_get_no_choice_and_the_services_are_not_asked(int permissions)
        {
            await using var server = await ServerWithRadarrAsync(permissions);
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            Assert.Null(await service.GetRequestOptionsAsync(SeerrMediaType.Movie, CancellationToken.None));
            Assert.Empty(server.ServicePaths);
        }

        [Fact]
        public async Task A_series_asks_sonarr()
        {
            await using var server = await FakeServer.StartAsync();
            server.Permissions = 2;
            server.ServiceJson["sonarr"] = @"[{""id"":3,""is4k"":false,""isDefault"":true}]";
            server.ServiceJson["sonarr/3"] = @"{""server"":{""activeProfileId"":7},""profiles"":[{""id"":7,""name"":""HD-1080p""}]}";
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var options = await service.GetRequestOptionsAsync(SeerrMediaType.Tv, CancellationToken.None);

            Assert.Equal(new[] { "sonarr", "sonarr/3" }, server.ServicePaths);
            Assert.Equal(3, options.Profiles.Single().ServerId);
        }

        [Fact]
        public async Task A_4K_default_server_is_passed_over_for_the_first_standard_one()
        {
            await using var server = await FakeServer.StartAsync();
            server.Permissions = 2;
            server.ServiceJson["radarr"] = @"[
                {""id"":0,""is4k"":true,""isDefault"":true},
                {""id"":1,""is4k"":false,""isDefault"":false},
                {""id"":2,""is4k"":false,""isDefault"":false}]";
            server.ServiceJson["radarr/1"] = RadarrDetail;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var options = await service.GetRequestOptionsAsync(SeerrMediaType.Movie, CancellationToken.None);

            Assert.Equal(1, options.Profiles[0].ServerId);
        }

        [Theory]
        [InlineData(@"[{""id"":0,""is4k"":true,""isDefault"":true}]")]
        [InlineData("[]")]
        public async Task No_standard_server_means_no_choice(string servers)
        {
            await using var server = await FakeServer.StartAsync();
            server.Permissions = 2;
            server.ServiceJson["radarr"] = servers;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            Assert.Null(await service.GetRequestOptionsAsync(SeerrMediaType.Movie, CancellationToken.None));
        }

        [Fact]
        public async Task Without_a_known_default_profile_the_first_is_chosen()
        {
            await using var server = await ServerWithRadarrAsync(2);
            server.ServiceJson["radarr/0"] = @"{""server"":{},""profiles"":[{""id"":1,""name"":""Any""},{""id"":4,""name"":""HD""}]}";
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var options = await service.GetRequestOptionsAsync(SeerrMediaType.Movie, CancellationToken.None);

            Assert.Equal(0, options.DefaultProfileIndex);
        }

        [Fact]
        public async Task No_profiles_means_no_choice()
        {
            await using var server = await ServerWithRadarrAsync(2);
            server.ServiceJson["radarr/0"] = @"{""server"":{""activeProfileId"":5},""profiles"":[]}";
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            Assert.Null(await service.GetRequestOptionsAsync(SeerrMediaType.Movie, CancellationToken.None));
        }

        [Fact]
        public async Task An_unreachable_service_means_no_choice()
        {
            await using var server = await FakeServer.StartAsync();
            server.Permissions = 2;
            server.ServiceJson["radarr"] = RadarrServers;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            Assert.Null(await service.GetRequestOptionsAsync(SeerrMediaType.Movie, CancellationToken.None));
        }

        [Fact]
        public async Task A_chosen_profile_goes_with_its_server_and_is4k_false()
        {
            await using var server = await FakeServer.StartAsync();
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);
            var profile = new SeerrQualityProfile { ServerId = 3, Id = 7, Name = "HD-1080p" };

            await service.RequestAsync(SeerrMediaType.Tv, 1399, new[] { 2 }, profile, CancellationToken.None);

            Assert.Equal(
                "{\"mediaType\":\"tv\",\"mediaId\":1399,\"seasons\":[2],\"serverId\":3,\"profileId\":7,\"is4k\":false}",
                server.LastRequestBody);
        }

        [Fact]
        public async Task A_downloading_film_shows_its_progress()
        {
            await using var server = await FakeServer.StartAsync();
            server.MediaJson["movie/11"] = @"{""id"":11,""title"":""Star Wars"",""mediaInfo"":{""status"":3,
                ""downloadStatus"":[{""size"":4000000000,""sizeLeft"":1000000000,""timeLeft"":""00:18:20"",""status"":""downloading""}]}}";
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var film = await service.GetMediaAsync(SeerrMediaType.Movie, 11, CancellationToken.None);

            Assert.Equal(75, film.Download.Percent);
            Assert.Equal(TimeSpan.FromSeconds(1100), film.Download.TimeLeft);
            Assert.Equal("Downloading · 75% · 18 min left", film.Download.Text);
        }

        [Fact]
        public async Task A_series_adds_up_its_episodes_and_waits_on_the_slowest()
        {
            await using var server = await FakeServer.StartAsync();
            server.MediaJson["tv/5"] = @"{""id"":5,""name"":""Show"",""seasons"":[],""mediaInfo"":{""status"":3,""downloadStatus"":[
                {""size"":100,""sizeLeft"":100,""timeLeft"":""1.02:05:00""},
                {""size"":200,""sizeLeft"":0,""timeLeft"":""00:00:00""},
                {""size"":100,""sizeLeft"":50,""timeLeft"":null}]}}";
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var show = await service.GetMediaAsync(SeerrMediaType.Tv, 5, CancellationToken.None);

            Assert.Equal(63, show.Download.Percent);
            Assert.Equal("Downloading 3 episodes · 63% · 26 h 5 min left", show.Download.Text);
        }

        [Fact]
        public async Task Progress_without_an_estimate_or_a_size_says_what_it_can()
        {
            await using var server = await FakeServer.StartAsync();
            server.MediaJson["movie/12"] = @"{""id"":12,""mediaInfo"":{""status"":3,
                ""downloadStatus"":[{""size"":0,""sizeLeft"":0,""timeLeft"":null}]}}";
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var film = await service.GetMediaAsync(SeerrMediaType.Movie, 12, CancellationToken.None);

            Assert.Equal("Downloading · 0%", film.Download.Text);
        }

        [Theory]
        [InlineData(@"{""status"":3,""downloadStatus"":[]}")]
        [InlineData(@"{""status"":3}")]
        [InlineData("null")]
        public async Task Nothing_downloading_means_no_progress(string mediaInfo)
        {
            await using var server = await FakeServer.StartAsync();
            server.MediaJson["movie/13"] = $"{{\"id\":13,\"mediaInfo\":{mediaInfo}}}";
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var film = await service.GetMediaAsync(SeerrMediaType.Movie, 13, CancellationToken.None);

            Assert.Null(film.Download);
        }

        [Theory]
        [InlineData("00:00:40", "under a minute")]
        [InlineData("00:01:29", "1 min")]
        [InlineData("00:59:45", "1 h 0 min")]
        [InlineData("02:29:31", "2 h 30 min")]
        public void Time_left_reads_in_whole_minutes(string timeLeft, string expected)
        {
            var download = new SeerrDownload { Percent = 10, TimeLeft = TimeSpan.Parse(timeLeft) };

            Assert.Equal($"Downloading · 10% · {expected} left", download.Text);
        }
    }
}
