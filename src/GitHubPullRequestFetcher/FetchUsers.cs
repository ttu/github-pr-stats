using JsonFlatFileDataStore;
using Newtonsoft.Json;
using Polly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace GitHubPullRequestFetcher
{
    internal class FetchUsers
    {
        private string _url = "https://api.github.com/search/users?per_page=100&q=location:finland+repos:%3E5+followers:%3E5&page=";

        private readonly IDataStore _dataStore;
        private readonly HttpClient _client;
        private readonly Waiter _waiter;
        private readonly List<Task> _saveTasks = new List<Task>();

        private List<UsersRequest> _allRequests = new List<UsersRequest>();

        public FetchUsers(IDataStore dataStore, HttpClient client, Waiter waiter)
        {
            _dataStore = dataStore;
            _client = client;
            _waiter = waiter;
        }

        public void Execute() => ExecuteAsync().GetAwaiter().GetResult();

        public async Task ExecuteAsync()
        {
            await Policy
              .Handle<FetchFailedException>()
              .WaitAndRetryAsync(int.MaxValue, (i) => _waiter.GetWaitTime())
              .ExecuteAsync(async () => await StartFetching());
        }

        private async Task StartFetching()
        {
            if (_allRequests.All(e => e.Items != null))
                _allRequests = await StartNewUpdate();

            await GetUsersList();
        }


        private async Task<List<UsersRequest>> StartNewUpdate()
        {
            int pageNum = 1;
            var response = await _client.GetAsync($"{_url}{pageNum}");

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var requestsLeft = response.Headers.Single(h => h.Key == "X-RateLimit-Remaining").Value;
                var reset = response.Headers.Single(h => h.Key == "X-RateLimit-Reset").Value?.First();
                _waiter.RateLimitReset = reset;
                Console.WriteLine("First user request failed");
                throw new FetchFailedException();
            }

            var links = response.Headers.Single(h => h.Key == "Link").Value;
            var lastLink = links.First().Split(',').Last();
            var start = lastLink.IndexOf("&page=") + 6;
            var end = lastLink.IndexOf(">");
            var lastPageNum = Convert.ToInt32(lastLink.Substring(start, end - start));

            var allUserRequests = Enumerable.Range(1, lastPageNum).Select(idx => new UsersRequest { Page = idx }).ToList();

            var json = await response.Content.ReadAsStringAsync();
            var resultObject = JsonConvert.DeserializeObject<UserResponseResult>(json);

            var r = new UsersRequest { Page = pageNum, Items = resultObject.Items };
            allUserRequests[0] = r;

            return allUserRequests;
        }

        private async Task GetUsersList()
        {
            int skip = 0;
            _saveTasks.Clear();

            var toFetch = _allRequests.Where(e => e.Items == null).ToList();

            Console.WriteLine($"GetUsersList requests: {toFetch.Count}");

            while (toFetch.Any(e => e.Items == null))
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

            Console.WriteLine("GetUsersList ready");
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
            var tasks = requestBatch.Where(e => e.Items == null)?.Select(async request =>
            {
                //Console.WriteLine($"Request: {request.Page}");

                try
                {
                    var response = await _client.GetAsync($"{_url}{request.Page}");

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        _waiter.RateLimitReset = response.Headers.SingleOrDefault(h => h.Key == "X-RateLimit-Reset").Value?.First();
                        //Console.WriteLine($"Request fail: {request.Page}");
                        return null;
                    }

                    //Console.WriteLine($"Request ok: {request.Page}");

                    var content = await response.Content.ReadAsStringAsync();
                    var resultObject = JsonConvert.DeserializeObject<UserResponseResult>(content);
                    var userRequest = requestBatch.Single(e => e.Page == request.Page);
                    userRequest.Items = resultObject.Items;
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