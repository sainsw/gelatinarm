using System;
using System.Net.Http;
using Gelatinarm.Shared.Server;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gelatinarm.Seerr.Tests.Fakes
{
    public class FakeAuthentication : IAuthenticationService
    {
        public string ServerUrl { get; set; }
        public string AccessToken { get; set; }
        public event EventHandler UserChanged;

        public void ChangeUser()
        {
            UserChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    ///     A SeerrService wired as App.ConfigureServices wires it: the named Jellyfin HttpClient and
    ///     an SDK client signed in as the user.
    /// </summary>
    public static class ServiceFactory
    {
        public static SeerrService Create(string serverUrl, string token, out FakeAuthentication auth,
            ILoggerFactory loggerFactory = null)
        {
            loggerFactory ??= NullLoggerFactory.Instance;

            var services = new ServiceCollection();
            services.AddHttpClient(SystemConstants.JellyfinHttpClientName);
            var provider = services.BuildServiceProvider();

            var settings = new JellyfinSdkSettings();
            settings.Initialize("Gelatinarm-tests", "0.0.0", "tests", "tests-device");
            settings.SetServerUrl(serverUrl);
            settings.SetAccessToken(token);
            var adapter = new JellyfinRequestAdapter(new JellyfinAuthenticationProvider(settings), settings, new HttpClient());

            auth = new FakeAuthentication { ServerUrl = serverUrl, AccessToken = token };
            return new SeerrService(loggerFactory.CreateLogger<SeerrService>(), auth, new JellyfinApiClient(adapter),
                provider.GetRequiredService<IHttpClientFactory>());
        }
    }
}
