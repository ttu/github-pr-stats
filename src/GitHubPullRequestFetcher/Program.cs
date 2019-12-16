using FluentScheduler;
using JsonFlatFileDataStore;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace GitHubStats
{
    internal class Program
    {
        private static void Main()
        {
            Log.Logger = new LoggerConfiguration()
                                    .Enrich.FromLogContext()
                                    .WriteTo.Console(
                                        theme: AnsiConsoleTheme.Code,
                                        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message}{NewLine}{Exception}")
                                    .MinimumLevel.Information()
                                    .CreateLogger();

            var token = "TOKEN_HERE";
            var user = "USERNAME_HERE";
            var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{token}"));

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authString);
            httpClient.DefaultRequestHeaders.Add("User-Agent", $"{user}");

            using var dataStore = new DataStore("data.json");
            var waiter = new Waiter();

            var registry = new Registry();
            registry.Schedule(() => new FetchUsers(dataStore, httpClient, waiter, Log.ForContext<FetchUsers>()).Execute()).ToRunNow().AndEvery(1).Days();
            registry.Schedule(() => new FetchPullRequests(dataStore, httpClient, waiter, Log.ForContext<FetchPullRequests>()).Execute()).ToRunNow().AndEvery(4).Hours();
            registry.Schedule(() => new FetchUserPrList(dataStore, httpClient, waiter, Log.ForContext<FetchUserPrList>()).Execute()).ToRunNow().AndEvery(12).Hours();

            JobManager.Initialize(registry);

            Log.Information("Running");
            Console.ReadLine();
        }
    }
}