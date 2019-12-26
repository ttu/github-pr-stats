using FluentScheduler;
using JsonFlatFileDataStore;
using Microsoft.Extensions.Configuration;
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
        private static readonly IConfiguration _config = new ConfigurationBuilder()
                                        .AddJsonFile("appsettings.json")
                                        .Build();

        private static void Main()
        {
            Log.Logger = CreateLogger();

            var token = _config["gitHub:token"];
            var user = _config["gitHub:userName"];
            var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{token}"));

            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authString);
            httpClient.DefaultRequestHeaders.Add("User-Agent", $"{user}");

            var dataStore = new DataStore(_config["dataStore:fileName"]);
            var waiter = new Waiter();

            var fetchUsers = new FetchUsers(dataStore, httpClient, waiter, Log.ForContext<FetchUsers>());
            var fetchPullRequests = new FetchPullRequests(dataStore, httpClient, waiter, Log.ForContext<FetchPullRequests>());
            var fetchAllPrItems = new FetchAllPullRequestItems(dataStore, httpClient, waiter, Log.ForContext<FetchAllPullRequestItems>());

            // Scheduling is handled in the application, so this should be running all the time
            int mode = 1;

            var registry = new Registry();

            if (mode == 0)
            {
                // New users are fetched once per day
                // User PR count is updated every 4 hours
                // User PR Items are updated every 12 hours
                registry.Schedule(() => fetchUsers.Execute()).ToRunNow().AndEvery(1).Days();
                registry.Schedule(() => fetchPullRequests.Execute()).ToRunNow().AndEvery(4).Hours();
                registry.Schedule(() => fetchAllPrItems.Execute()).ToRunNow().AndEvery(12).Hours();
            }
            else
            {
                // Run each task in order
                // Repeat every 4 hours
                registry.Schedule(() => fetchUsers.Execute())
                        .AndThen(() => fetchPullRequests.Execute())
                        .AndThen(() => fetchAllPrItems.Execute())
                        .ToRunNow()
                        .AndEvery(4).Hours();
            }

            JobManager.Initialize(registry);

            Log.Information("Running. Press any key to exit.");
            Console.ReadLine();
        }

        private static ILogger CreateLogger() =>
                        new LoggerConfiguration()
                            .Enrich.FromLogContext()
                            .WriteTo.Console(
                                theme: AnsiConsoleTheme.Code,
                                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message}{NewLine}{Exception}")
                            .MinimumLevel.Information()
                            .CreateLogger();
    }
}