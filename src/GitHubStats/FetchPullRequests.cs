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
    /// Update users PR count and max first 100 PR infos
    /// </summary>
    internal class FetchPullRequests
    {
        private readonly IDataStore _dataStore;
        private readonly HttpClient _client;
        private readonly Waiter _waiter;
        private readonly ILogger _log;
        private readonly List<Task> _saveTasks = new List<Task>();

        private List<User> _allRequests = new List<User>();

        public FetchPullRequests(IDataStore dataStore, HttpClient client, Waiter waiter, ILogger log)
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
            if (_allRequests.All(e => e.PR_Count != e.Items.Count()))
                _allRequests = _dataStore.GetCollection<User>().AsQueryable().ToList();

            if (_allRequests.Count == 0)
            {
                _log.Information("Users not fetched to datastore. Waiting for FetchUsers");
                throw new FetchFailedException();
            }

            await GetPullRequests();
        }

        private async Task GetPullRequests()
        {
            int skip = 0;
            _saveTasks.Clear();

            var updateStart = DateTimeOffset.UtcNow;

            var toFetch = _allRequests.Where(e => e?.Last_Update.Date < updateStart.Date).OrderBy(e => e?.Last_Update).ToList();

            while (toFetch.Any(e => e?.Last_Update.Date < updateStart.Date))
            {
                _log.Information("{Task} requests: {FetchCount}", "Update user PR Count", toFetch.Count - skip);

                var datas = await HandleBatch(toFetch.Skip(skip).Take(Constants.BATCH_SIZE), updateStart);

                var batchSaveTask = _dataStore.SaveBatch(datas);
                _saveTasks.Add(batchSaveTask);

                if (datas.Any(e => e == null))
                {
                    _log.Information("Waiting for save tasks: {SaveCount}", _saveTasks.Where(t => !t.IsCompleted).Count());
                    await Task.WhenAll(_saveTasks);
                    _saveTasks.Clear();
                    throw new FetchFailedException();
                }

                skip += Constants.BATCH_SIZE;
            }

            await Task.WhenAll(_saveTasks);
            _saveTasks.Clear();

            _log.Information("{Task} ready", "Update User PR Count");
        }

        private async Task<IEnumerable<User>> HandleBatch(IEnumerable<User> usersBatch, DateTimeOffset updateStamp)
        {
            var tasks = usersBatch.Where(e => e?.Last_Update.Date < updateStamp.Date)?.Select(async user =>
            {
                _log.Debug("Request: {UserLogin}", user.Login);

                try
                {
                    var response = await _client.GetAsync($"https://api.github.com/search/issues?per_page=100&q=author%3A{user.Login}+type%3Apr");

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        _waiter.RateLimitResetTime = response.Headers.SingleOrDefault(h => h.Key == Constants.RATE_LIMIT_HEADER).Value?.First() ?? "";
                        _log.Debug("Request fail: {UserLogin}", user.Login);
                        return null;
                    }

                    _log.Debug("Request ok: {UserLogin}", user.Login);

                    var content = await response.Content.ReadAsStringAsync();
                    var result = JsonConvert.DeserializeObject<PrResponse>(content);
                    // Why is result Total_Count sometimes 0?
                    user.PR_Count = result.Total_Count == 0 && result.Items.Count > 0 ? -1 : result.Total_Count;
                    user.Items = result.Items;
                    user.Last_Update = updateStamp;

                    return user;
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