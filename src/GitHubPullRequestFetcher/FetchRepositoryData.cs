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

namespace GitHubPullRequestFetcher
{
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

            _log.Information($"Update user PR count: {toFetch.Count}");

            while (toFetch.Any(e => e?.Last_Update.Date < updateStart.Date))
            {
                var datas = await HandleBatch(toFetch.Skip(skip).Take(Constants.BATCH_SIZE), updateStart);

                var saveTask = SaveBatch(datas);
                _saveTasks.Add(saveTask);

                if (datas.Any(e => e == null))
                {
                    _log.Debug($"Waiting for save tasks: {_saveTasks.Count}");
                    await Task.WhenAll(_saveTasks);
                    throw new FetchFailedException();
                }

                skip += Constants.BATCH_SIZE;
            }

            _log.Information("Update user PR count ready");
        }

        private async Task SaveBatch(IEnumerable<User> datas)
        {
            var tasks = datas.Where(e => e != null)
                             .Select(user =>
                             {
                                 return Task.Run(async () =>
                                 {
                                     var fromDb = _dataStore.GetCollection<User>().AsQueryable().First(e => e.GitHubId == user.GitHubId);
                                     fromDb.Last_Update = user.Last_Update;
                                     fromDb.PR_Count = user.PR_Count;

                                     if (fromDb.PR_Count != fromDb.Items.Count)
                                     {
                                         foreach (var i in user.Items)
                                         {
                                             if (fromDb.Items.Any(e => e.Id == i.Id) == false)
                                                 fromDb.Items.Add(i);
                                         };
                                     }

                                     await _dataStore.GetCollection<User>().ReplaceOneAsync(e => e.Id == fromDb.Id, fromDb);
                                 });
                             });

            await Task.WhenAll(tasks);
        }

        private async Task<IEnumerable<User>> HandleBatch(IEnumerable<User> usersBatch, DateTimeOffset updateStamp)
        {
            var tasks = usersBatch.Where(e => e?.Last_Update.Date < updateStamp.Date)?.Select(async user =>
            {
                _log.Debug($"Request: {user.Login}");

                try
                {
                    var response = await _client.GetAsync($"https://api.github.com/search/issues?per_page=100&q=author%3A{user.Login}+type%3Apr");

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        _waiter.RateLimitResetTime = response.Headers.SingleOrDefault(h => h.Key == Constants.RATE_LIMIT_HEADER).Value?.First() ?? "";
                        _log.Debug($"Request fail: {user.Login}");
                        return null;
                    }

                    _log.Debug($"Request ok: {user.Login}");

                    var content = await response.Content.ReadAsStringAsync();
                    var resultObject = JsonConvert.DeserializeObject<PrResponse>(content);
                    user.PR_Count = resultObject.Total_Count;
                    user.Items = resultObject.Items;
                    user.Last_Update = updateStamp;
                    user.PR_Count = user.PR_Count == 0 && user.Items.Count > 0 ? -1 : user.PR_Count;

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