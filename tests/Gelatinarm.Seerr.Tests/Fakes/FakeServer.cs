using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Seerr.Tests.Fakes
{
    /// <summary>
    ///     A Jellyfin server with the Streamyfin plugin and a Seerr, on one localhost port (Seerr
    ///     under /seerr). Each knob is what a test changes; each record is what it checks.
    /// </summary>
    public sealed class FakeServer : IAsyncDisposable
    {
        public const string Token = "user-token";
        public const string QuickConnectCode = "123456";
        public const string QuickConnectSecret = "qc-secret";
        public const string SessionCookie = "connect.sid=s%3Asession.sig";

        private WebApplication _app;

        // Jellyfin
        public int ConfigStatus { get; set; } = 200;
        public string SeerrUrlSetting { get; set; }
        public bool AuthorizeAllowed { get; set; } = true;

        // Seerr
        public int InitiateStatus { get; set; } = 200;
        public int AuthenticateStatus { get; set; } = 200;

        // Set-Cookie header lines sent on sign-in; one line may fold several cookies
        public List<string> AuthenticateCookies { get; set; } = new() { SessionCookie + "; Path=/; HttpOnly" };

        public string SearchJson { get; set; } = "{\"results\":[]}";

        // The signed-in Seerr user's Permission flags (2: admin)
        public int Permissions { get; set; } = 32;

        // /service/radarr and /service/sonarr, and /service/{name}/{id}
        public Dictionary<string, string> ServiceJson { get; } = new();
        public Dictionary<string, string> MediaJson { get; } = new();

        // Answers given before the normal one, in order: to fake an expired session or a refusal
        public Queue<int> SearchStatusOverrides { get; } = new();
        public Queue<int> RequestStatuses { get; } = new();

        public string BaseUrl { get; private set; }
        public string SeerrUrl => BaseUrl + "/seerr";

        public int ConfigCalls;
        public int InitiateCalls;
        public int AuthorizeCalls;
        public int AuthenticateCalls;
        public int SearchCalls;
        public string LastAuthorizeHeader;
        public string LastAuthorizeCode;
        public string LastAuthenticateBody;
        public string LastSearchQueryString;
        public string LastRequestBody;
        public ConcurrentQueue<string> ServicePaths { get; } = new();
        public ConcurrentQueue<string> SeerrCookies { get; } = new();

        public static async Task<FakeServer> StartAsync()
        {
            var server = new FakeServer();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            server.Map(app);
            await app.StartAsync();

            server._app = app;
            server.BaseUrl = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>().Addresses.First();
            server.SeerrUrlSetting ??= server.SeerrUrl;
            return server;
        }

        private void Map(WebApplication app)
        {
            app.MapGet("/streamyfin/config", (HttpContext ctx) =>
            {
                ConfigCalls++;
                if (!HasUserToken(ctx))
                {
                    return Results.StatusCode(401);
                }

                if (ConfigStatus != 200)
                {
                    return Results.StatusCode(ConfigStatus);
                }

                var value = SeerrUrlSetting == null ? "null" : $"\"{SeerrUrlSetting}\"";
                return Results.Text(
                    $"{{\"settings\":{{\"jellyseerrServerUrl\":{{\"locked\":false,\"value\":{value}}}}}}}",
                    "application/json");
            });

            app.MapPost("/QuickConnect/Authorize", (HttpContext ctx) =>
            {
                AuthorizeCalls++;
                LastAuthorizeHeader = ctx.Request.Headers.Authorization.ToString();
                LastAuthorizeCode = ctx.Request.Query["code"];
                if (!AuthorizeAllowed)
                {
                    return Results.StatusCode(403);
                }

                return Results.Text(LastAuthorizeCode == QuickConnectCode ? "true" : "false", "application/json");
            });

            app.MapPost("/seerr/api/v1/auth/jellyfin/quickconnect/initiate", () =>
            {
                InitiateCalls++;
                return InitiateStatus != 200
                    ? Results.StatusCode(InitiateStatus)
                    : Results.Text($"{{\"code\":\"{QuickConnectCode}\",\"secret\":\"{QuickConnectSecret}\"}}",
                        "application/json");
            });

            app.MapPost("/seerr/api/v1/auth/jellyfin/quickconnect/authenticate", async (HttpContext ctx) =>
            {
                AuthenticateCalls++;
                LastAuthenticateBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
                if (AuthenticateStatus != 200)
                {
                    return Results.StatusCode(AuthenticateStatus);
                }

                foreach (var cookie in AuthenticateCookies)
                {
                    ctx.Response.Headers.Append("Set-Cookie", cookie);
                }

                return Results.Text("{\"id\":1}", "application/json");
            });

            app.MapGet("/seerr/api/v1/search", (HttpContext ctx) =>
            {
                SearchCalls++;
                LastSearchQueryString = ctx.Request.QueryString.Value;
                SeerrCookies.Enqueue(ctx.Request.Headers.Cookie.ToString());
                if (SearchStatusOverrides.Count > 0)
                {
                    return Results.StatusCode(SearchStatusOverrides.Dequeue());
                }

                return HasSession(ctx) ? Results.Text(SearchJson, "application/json") : Results.StatusCode(403);
            });

            app.MapGet("/seerr/api/v1/auth/me", (HttpContext ctx) =>
                HasSession(ctx)
                    ? Results.Text($"{{\"id\":1,\"permissions\":{Permissions}}}", "application/json")
                    : Results.StatusCode(403));

            app.MapGet("/seerr/api/v1/service/{**path}", (HttpContext ctx, string path) =>
            {
                ServicePaths.Enqueue(path);
                if (!HasSession(ctx))
                {
                    return Results.StatusCode(403);
                }

                return ServiceJson.TryGetValue(path, out var json)
                    ? Results.Text(json, "application/json")
                    : Results.StatusCode(404);
            });

            app.MapGet("/seerr/api/v1/{type}/{id}", (HttpContext ctx, string type, string id) =>
            {
                SeerrCookies.Enqueue(ctx.Request.Headers.Cookie.ToString());
                if (!HasSession(ctx))
                {
                    return Results.StatusCode(403);
                }

                return MediaJson.TryGetValue($"{type}/{id}", out var json)
                    ? Results.Text(json, "application/json")
                    : Results.StatusCode(404);
            });

            app.MapPost("/seerr/api/v1/request", async (HttpContext ctx) =>
            {
                LastRequestBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
                if (!HasSession(ctx))
                {
                    return Results.StatusCode(403);
                }

                return Results.StatusCode(RequestStatuses.Count > 0 ? RequestStatuses.Dequeue() : 201);
            });
        }

        private static bool HasUserToken(HttpContext ctx)
        {
            return ctx.Request.Headers.Authorization.ToString().Contains($"Token=\"{Token}\"", StringComparison.Ordinal);
        }

        private static bool HasSession(HttpContext ctx)
        {
            return ctx.Request.Headers.Cookie.ToString() == SessionCookie;
        }

        public async ValueTask DisposeAsync()
        {
            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
        }
    }
}
