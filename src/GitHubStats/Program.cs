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
        private static void Main()
        {
            var config = new ConfigurationBuilder()
                                         .AddJsonFile("appsettings.json")
                                         .Build();

            Log.Logger = CreateLogger();

            var token = config["gitHub:token"];
            var user = config["gitHub:userName"];
            var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{token}"));

            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authString);
            httpClient.DefaultRequestHeaders.Add("User-Agent", $"{user}");

            var dataStore = new DataStore(config["dataStore:fileName"]);
            var waiter = new Waiter();

            var fetchUsers = new FetchUsers(dataStore, httpClient, waiter, Log.ForContext<FetchUsers>());
            var fetchPullRequests = new FetchPullRequests(dataStore, httpClient, waiter, Log.ForContext<FetchPullRequests>());
            var userPrListTask = new FetchAllPullRequestItems(dataStore, httpClient, waiter, Log.ForContext<FetchAllPullRequestItems>());

            var registry = new Registry();
            registry.Schedule(() => fetchUsers.Execute()).ToRunNow().AndEvery(1).Days();
            registry.Schedule(() => fetchPullRequests.Execute()).ToRunNow().AndEvery(4).Hours();
            registry.Schedule(() => userPrListTask.Execute()).ToRunNow().AndEvery(12).Hours();

            JobManager.Initialize(registry);

            Log.Information("Running");
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