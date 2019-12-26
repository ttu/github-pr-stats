using JsonFlatFileDataStore;
using Newtonsoft.Json;
using Polly;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace GitHubStats
{
    /// <summary>
    /// Fetch repositories and languages used
    /// Repository API doesn't follow same Rate-Limit. It will have longer wait time after 1000(?) requests
    /// TODO: Update existing Repositories
    /// </summary>
    internal class FetchRepositories
    {
        private readonly IDataStore _dataStore;
        private readonly HttpClient _client;
        private readonly Waiter _waiter;
        private readonly ILogger _log;
        private readonly List<Task> _saveTasks = new List<Task>();

        private List<UsersRequest> _allRequests = new List<UsersRequest>();

        public FetchRepositories(IDataStore dataStore, HttpClient client, Waiter waiter, ILogger log)
        {
            _dataStore = dataStore;
            _client = client;
            _waiter = waiter;
            _log = log;
        }

        public void Execute() => ExecuteAsync().GetAwaiter().GetResult();

        public async Task ExecuteAsync()
        {
            await Policy
              .Handle<FetchFailedException>()
              .WaitAndRetryAsync(int.MaxValue, (i) => _waiter.GetWaitTimeAndLog(_log))
              .ExecuteAsync(async () => await StartFetching());
        }

        private async Task StartFetching()
        {
            var storedRepos = _dataStore.GetCollection<Repository>().AsQueryable().ToList();
            var allUsers = _dataStore.GetCollection<User>().AsQueryable().ToList();

            var distinctRepoUrls = allUsers.SelectMany(u => u.Items.Select(i => i.Repository_Url).Distinct()).Distinct();

            var toFetch = distinctRepoUrls.Where(e => !storedRepos.Any(r => r.Repository_Url == e)).Distinct().ToList();
            var fetchRepos = toFetch.Select(url => new Repository
            {
                Repository_Url = url
            }).ToList();

            await GetRepositories(fetchRepos);
        }

        private async Task GetRepositories(List<Repository> reposToFetch)
        {
            _saveTasks.Clear();

            while (reposToFetch.Any())
            {
                _log.Information("{Task} requests: {FetchCount}", "GetRepositories", reposToFetch.Count);

                var datas = await HandleBatch(reposToFetch.Take(Constants.BATCH_SIZE));

                var saveTask = InsertNewRepositories(datas);
                _saveTasks.Add(saveTask);

                if (datas.Any(e => e == null))
                {
                    _log.Information("Waiting for save tasks: {SaveCount}", _saveTasks.Where(t => !t.IsCompleted).Count());
                    await Task.WhenAll(_saveTasks);
                    throw new FetchFailedException();
                }

                var received = datas.Where(d => d != null).ToList();
                received.ForEach(r => reposToFetch.Remove(r));

                var inProgressCount = _saveTasks.Where(t => !t.IsCompleted).Count();

                if (inProgressCount > 50)
                {
                    _log.Information("Save task max count reached: {SaveCount}", inProgressCount);
                    await Task.WhenAll(_saveTasks);
                    _saveTasks.Clear();
                }
            }

            _log.Information("Saving: {SaveCount}", _saveTasks.Where(t => !t.IsCompleted).Count());

            await Task.WhenAll(_saveTasks);
            _saveTasks.Clear();

            _log.Information("{Task} ready", "GetRepositories");
        }

        private async Task InsertNewRepositories(IEnumerable<Repository> datas)
        {
            var toAdd = datas.Where(e => e != null).ToList();

            if (!toAdd.Any())
                return;

            var collection = _dataStore.GetCollection<Repository>();
            await collection.InsertManyAsync(toAdd);
        }

        private async Task<IEnumerable<Repository>> HandleBatch(IEnumerable<Repository> requestBatch)
        {
            var tasks = requestBatch.Select(async repo =>
            {
                _log.Debug("Request: {RepoUrl}", repo.Repository_Url);

                var updateTime = DateTimeOffset.UtcNow;

                try
                {
                    var response = await _client.GetAsync($"{repo.Repository_Url}/languages");

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        _waiter.RateLimitResetTime = response.Headers.SingleOrDefault(h => h.Key == Constants.RATE_LIMIT_HEADER).Value?.First() ?? "";
                        _log.Debug("Request fail: {RepoUrl}", repo.Repository_Url);
                        return null;
                    }

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        _log.Information("Request notFound: {RepoUrl}", repo.Repository_Url);
                        repo.Last_Update = updateTime;
                        return repo;
                    }

                    _log.Debug("Request ok: {RepoUrl}", repo.Repository_Url);

                    var content = await response.Content.ReadAsStringAsync();
                    var languages = JsonConvert.DeserializeObject<Dictionary<string, long>>(content);

                    repo.Languages = languages;
                    repo.Last_Update = updateTime;
                    return repo;
                }
                catch (HttpRequestException)
                {
                    return null;
                }
            });

            return await Task.WhenAll(tasks);
        }
    }
}