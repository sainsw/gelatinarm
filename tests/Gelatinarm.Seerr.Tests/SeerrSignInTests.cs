using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Seerr.Tests.Fakes;
using Xunit;

namespace Gelatinarm.Seerr.Tests
{
    // Finding Seerr through the Streamyfin plugin, the Quick Connect sign-in, and the session's life
    public class SeerrSignInTests
    {
        private const string OneMovie =
            "{\"results\":[{\"mediaType\":\"movie\",\"id\":11,\"title\":\"Star Wars\",\"releaseDate\":\"1977-05-25\"}]}";

        [Fact]
        public async Task Signs_in_with_quick_connect_approved_by_the_users_own_session()
        {
            await using var server = await FakeServer.StartAsync();
            server.SearchJson = OneMovie;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var results = await service.SearchAsync("star wars", CancellationToken.None);

            Assert.Single(results);
            Assert.Equal(1, server.InitiateCalls);
            Assert.Equal(FakeServer.QuickConnectCode, server.LastAuthorizeCode);
            Assert.Contains($"Token=\"{FakeServer.Token}\"", server.LastAuthorizeHeader);
            Assert.Equal($"{{\"secret\":\"{FakeServer.QuickConnectSecret}\"}}", server.LastAuthenticateBody);
            Assert.All(server.SeerrCookies, cookie => Assert.Equal(FakeServer.SessionCookie, cookie));
            Assert.Empty(service.Errors.Errors);
        }

        [Fact]
        public async Task Reuses_the_session_across_requests()
        {
            await using var server = await FakeServer.StartAsync();
            server.SearchJson = OneMovie;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            await service.SearchAsync("a", CancellationToken.None);
            await service.SearchAsync("b", CancellationToken.None);

            Assert.Equal(1, server.ConfigCalls);
            Assert.Equal(1, server.InitiateCalls);
            Assert.Equal(2, server.SearchCalls);
        }

        [Theory]
        [InlineData(401)]
        [InlineData(403)]
        public async Task Signs_in_again_once_when_the_session_is_refused(int status)
        {
            await using var server = await FakeServer.StartAsync();
            server.SearchJson = OneMovie;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);
            await service.SearchAsync("warm up", CancellationToken.None);

            server.SearchStatusOverrides.Enqueue(status);
            var results = await service.SearchAsync("star wars", CancellationToken.None);

            Assert.Single(results);
            Assert.Equal(2, server.InitiateCalls);
        }

        [Fact]
        public async Task Gives_up_after_one_fresh_sign_in()
        {
            await using var server = await FakeServer.StartAsync();
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);
            server.SearchStatusOverrides.Enqueue(403);
            server.SearchStatusOverrides.Enqueue(403);

            var results = await service.SearchAsync("x", CancellationToken.None);

            Assert.Empty(results);
            Assert.Equal(2, server.SearchCalls);
        }

        [Fact]
        public async Task Without_the_plugin_seerr_is_never_contacted_and_nothing_is_reported()
        {
            await using var server = await FakeServer.StartAsync();
            server.ConfigStatus = 404;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            var results = await service.SearchAsync("x", CancellationToken.None);

            Assert.Empty(results);
            Assert.Equal(0, server.InitiateCalls);
            Assert.Empty(service.Errors.Errors);
        }

        [Theory]
        [InlineData("Enter jellyseerr server url")]
        [InlineData("ftp://seerr.example")]
        [InlineData(null)]
        public async Task A_plugin_without_a_seerr_url_leaves_requests_off(string setting)
        {
            await using var server = await FakeServer.StartAsync();
            server.SeerrUrlSetting = setting;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            Assert.Empty(await service.SearchAsync("x", CancellationToken.None));
            Assert.Equal(0, server.InitiateCalls);
        }

        [Fact]
        public async Task A_trailing_slash_on_the_seerr_url_is_dropped()
        {
            await using var server = await FakeServer.StartAsync();
            server.SeerrUrlSetting = server.SeerrUrl + "/";
            server.SearchJson = OneMovie;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            Assert.Single(await service.SearchAsync("x", CancellationToken.None));
        }

        [Fact]
        public async Task Jellyfin_refusing_the_code_turns_requests_off_until_the_user_changes()
        {
            await using var server = await FakeServer.StartAsync();
            server.AuthorizeAllowed = false;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            await service.SearchAsync("x", CancellationToken.None);
            await service.SearchAsync("y", CancellationToken.None);

            Assert.Equal(1, server.InitiateCalls);
            Assert.Equal(0, server.AuthenticateCalls);
            Assert.Equal(0, server.SearchCalls);
        }

        [Theory]
        [InlineData(404)] // Seerr before 3.4
        [InlineData(500)] // Quick Connect off on the Jellyfin server
        public async Task Seerr_not_starting_quick_connect_turns_requests_off(int status)
        {
            await using var server = await FakeServer.StartAsync();
            server.InitiateStatus = status;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            Assert.Empty(await service.SearchAsync("x", CancellationToken.None));
            Assert.Equal(0, server.AuthorizeCalls);
        }

        [Fact]
        public async Task Seerr_refusing_the_user_turns_requests_off()
        {
            await using var server = await FakeServer.StartAsync();
            server.AuthenticateStatus = 403;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            Assert.Empty(await service.SearchAsync("x", CancellationToken.None));
            Assert.Equal(0, server.SearchCalls);
        }

        [Fact]
        public async Task Finds_the_session_cookie_among_several_in_one_header()
        {
            await using var server = await FakeServer.StartAsync();
            server.AuthenticateCookies = new()
            {
                "XSRF-TOKEN=abc; Path=/, " + FakeServer.SessionCookie + "; Path=/; Expires=Mon, 21 Dec 2026 10:00:00 GMT; HttpOnly"
            };
            server.SearchJson = OneMovie;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            Assert.Single(await service.SearchAsync("x", CancellationToken.None));
        }

        [Fact]
        public async Task A_sign_in_without_a_session_cookie_turns_requests_off()
        {
            await using var server = await FakeServer.StartAsync();
            server.AuthenticateCookies = new() { "other=1; Path=/" };
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out _);

            Assert.Empty(await service.SearchAsync("x", CancellationToken.None));
            Assert.Equal(0, server.SearchCalls);
        }

        [Fact]
        public async Task A_user_change_drops_the_session_and_any_verdict()
        {
            await using var server = await FakeServer.StartAsync();
            server.ConfigStatus = 404;
            server.SearchJson = OneMovie;
            using var service = ServiceFactory.Create(server.BaseUrl, FakeServer.Token, out var auth);
            Assert.Empty(await service.SearchAsync("x", CancellationToken.None));

            server.ConfigStatus = 200;
            auth.ChangeUser();

            Assert.Single(await service.SearchAsync("x", CancellationToken.None));
            Assert.Equal(2, server.ConfigCalls);
        }

        [Fact]
        public async Task A_rejected_jellyfin_token_leaves_requests_off()
        {
            await using var server = await FakeServer.StartAsync();
            using var service = ServiceFactory.Create(server.BaseUrl, "not-the-token", out _);

            Assert.Empty(await service.SearchAsync("x", CancellationToken.None));
            Assert.Equal(0, server.InitiateCalls);
        }

        [Fact]
        public async Task An_unreachable_server_is_reported_and_tried_again_next_time()
        {
            using var service = ServiceFactory.Create("http://127.0.0.1:9", FakeServer.Token, out _);

            Assert.Empty(await service.SearchAsync("x", CancellationToken.None));
            Assert.Empty(await service.SearchAsync("x", CancellationToken.None));
            Assert.Equal(2, service.Errors.Errors.Count);
        }
    }
}
