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
    internal class FetchUsers
    {
        private static readonly string URL = $"https://api.github.com/search/users?per_page=100&q=location:{Constants.COUNTRY}+repos:%3E{Constants.MIN_REPOS}+followers:%3E{Constants.MIN_FOLLOWERS}&page=";

        private readonly IDataStore _dataStore;
        private readonly HttpClient _client;
        private readonly Waiter _waiter;
        private readonly ILogger _log;
        private readonly List<Task> _saveTasks = new List<Task>();

        private List<UsersRequest> _allRequests = new List<UsersRequest>();

        public FetchUsers(IDataStore dataStore, HttpClient client, Waiter waiter, ILogger log)
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
            if (_allRequests.All(e => e.Fetched))
                _allRequests = await StartNewUpdate();

            await GetUsersList();
        }

        private async Task<List<UsersRequest>> StartNewUpdate()
        {
            int pageNum = 1;
            var response = await _client.GetAsync($"{URL}{pageNum}");

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _log.Error("Unauthorized: Check username and token");
                throw new Exception("Check username and token");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                //var requestsLeft = response.Headers.Single(h => h.Key == "X-RateLimit-Remaining").Value;
                var resetTime = response.Headers.Single(h => h.Key == Constants.RATE_LIMIT_HEADER).Value?.First();
                _waiter.RateLimitResetTime = resetTime ?? "";
                _log.Information("First user request failed");
                throw new FetchFailedException();
            }

            var links = response.Headers.Single(h => h.Key == "Link").Value;
            var lastLink = links.First().Split(',').Last();
            var start = lastLink.IndexOf("&page=") + 6;
            var end = lastLink.IndexOf(">");
            var lastPageNum = int.Parse(lastLink[start..end]);

            var allUserRequests = Enumerable.Range(1, lastPageNum).Select(idx => new UsersRequest { Page = idx }).ToList();

            var json = await response.Content.ReadAsStringAsync();
            var resultObject = JsonConvert.DeserializeObject<UserResponseResult>(json);

            var r = new UsersRequest { Page = pageNum, Items = resultObject.Items, Fetched = true };
            allUserRequests[0] = r;

            return allUserRequests;
        }

        private async Task GetUsersList()
        {
            int skip = 0;
            _saveTasks.Clear();

            var toFetch = _allRequests.Where(e => !e.Fetched).ToList();

            _log.Information("{Task} requests: {FetchCount}", "GetUsersList", toFetch.Count);

            while (toFetch.Any(e => !e.Fetched))
            {
                var datas = await HandleBatch(toFetch.Skip(skip).Take(Constants.BATCH_SIZE));

                var saveTask = SaveBatch(datas);
                _saveTasks.Add(saveTask);

                if (datas.Any(e => e == null))
                {
                    await Task.WhenAll(_saveTasks);
                    throw new FetchFailedException();
                }

                skip += Constants.BATCH_SIZE;
            }

            _log.Information("{Task} ready", "GetUsersList");
        }

        private async Task SaveBatch(IEnumerable<UsersRequest> datas)
        {
            var users = datas.Where(e => e != null).SelectMany(e => e.Items).ToList();
            users.ForEach(u => u.GitHubId = u.Id);

            var collection = _dataStore.GetCollection<User>();
            var query = collection.AsQueryable();

            var toAdd = users.Where(u => query.Any(e => e.GitHubId == u.GitHubId) == false).ToList();
            await collection.InsertManyAsync(toAdd);
        }

        private async Task<IEnumerable<UsersRequest>> HandleBatch(IEnumerable<UsersRequest> requestBatch)
        {
            var tasks = requestBatch.Where(e => !e.Fetched)?.Select(async request =>
            {
                _log.Debug("Request: {RequestPage}", request.Page);

                try
                {
                    var response = await _client.GetAsync($"{URL}{request.Page}");

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        _waiter.RateLimitResetTime = response.Headers.SingleOrDefault(h => h.Key == Constants.RATE_LIMIT_HEADER).Value?.First() ?? "";
                        _log.Debug("Request fail: {RequestPage}", request.Page);
                        return null;
                    }

                    _log.Debug("Request ok: {RequestPage}", request.Page);

                    var content = await response.Content.ReadAsStringAsync();
                    var resultObject = JsonConvert.DeserializeObject<UserResponseResult>(content);
                    var userRequest = requestBatch.Single(e => e.Page == request.Page);
                    userRequest.Items = resultObject.Items;
                    userRequest.Fetched = true;
                    return userRequest;
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