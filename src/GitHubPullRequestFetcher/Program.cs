using FluentScheduler;
using JsonFlatFileDataStore;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace GitHubPullRequestFetcher
{
    internal class Program
    {
        private static void Main()
        {
            var token = "TOKEN_HERE";
            var user = "USERNAME_HERE";
            var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{token}"));

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authString);
            httpClient.DefaultRequestHeaders.Add("User-Agent", $"{user}");

            using var dataStore = new DataStore("data.json");
            var waiter = new Waiter();

            var registry = new Registry();
            registry.Schedule(() => new FetchUsers(dataStore, httpClient, waiter).Execute()).ToRunNow().AndEvery(1).Days();
            registry.Schedule(() => new FetchPullRequests(dataStore, httpClient, waiter).Execute()).ToRunNow().AndEvery(4).Hours();
            registry.Schedule(() => new FetchUserPrList(dataStore, httpClient, waiter).Execute()).ToRunNow().AndEvery(12).Hours();

            JobManager.Initialize(registry);

            Console.WriteLine("Running");
            Console.ReadLine();
        }
    }
}