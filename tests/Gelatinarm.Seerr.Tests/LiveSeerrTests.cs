using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Seerr.Tests.Fakes;
using Gelatinarm.Shared.Server;
using Xunit;

namespace Gelatinarm.Seerr.Tests
{
    /// <summary>
    ///     Against a real Jellyfin with the Streamyfin plugin and Seerr, signed in as a real user:
    ///     GELATINARM_TEST_SERVER (the Jellyfin URL) and GELATINARM_TEST_TOKEN (a user's access
    ///     token), optionally GELATINARM_TEST_QUERY (default "hercules"). Skipped without them.
    ///     Read-only: nothing is requested.
    /// </summary>
    public class LiveSeerrTests
    {
        private static readonly string Server = Environment.GetEnvironmentVariable("GELATINARM_TEST_SERVER");
        private static readonly string Token = Environment.GetEnvironmentVariable("GELATINARM_TEST_TOKEN");
        private static readonly string Query = Environment.GetEnvironmentVariable("GELATINARM_TEST_QUERY") ?? "hercules";

        [SkippableFact]
        public async Task Searches_and_reads_a_series_on_a_real_server()
        {
            Skip.If(string.IsNullOrEmpty(Server) || string.IsNullOrEmpty(Token),
                "GELATINARM_TEST_SERVER and GELATINARM_TEST_TOKEN are not set");
            using var service = ServiceFactory.Create(Server, Token, out _);

            var results = await service.SearchAsync(Query, CancellationToken.None);

            Assert.Empty(service.Errors.Errors);
            Assert.NotEmpty(results);
            Assert.All(results, r => Assert.NotEqual(SeerrMediaStatus.Available, r.Status));

            var series = results.FirstOrDefault(r => r.MediaType == SeerrMediaType.Tv);
            if (series != null)
            {
                var detail = await service.GetMediaAsync(series.MediaType, series.TmdbId, CancellationToken.None);
                Assert.NotNull(detail);
                Assert.All(detail.Seasons, s => Assert.True(s.SeasonNumber > 0));
            }
        }

        // Profiles come back only for users Seerr lets choose; either answer is valid, a failure is not
        [SkippableFact]
        public async Task Reads_request_options_on_a_real_server()
        {
            Skip.If(string.IsNullOrEmpty(Server) || string.IsNullOrEmpty(Token),
                "GELATINARM_TEST_SERVER and GELATINARM_TEST_TOKEN are not set");
            using var service = ServiceFactory.Create(Server, Token, out _);

            var movie = await service.GetRequestOptionsAsync(SeerrMediaType.Movie, CancellationToken.None);
            var tv = await service.GetRequestOptionsAsync(SeerrMediaType.Tv, CancellationToken.None);

            Assert.Empty(service.Errors.Errors);
            foreach (var options in new[] { movie, tv }.Where(o => o != null))
            {
                Assert.NotEmpty(options.Profiles);
                Assert.InRange(options.DefaultProfileIndex, 0, options.Profiles.Count - 1);
            }
        }
    }
}
