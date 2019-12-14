﻿using JsonFlatFileDataStore;
using Newtonsoft.Json;
using Polly;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace GitHubPullRequestFetcher
{
    internal class FetchUserPrList
    {
        private readonly IDataStore _dataStore;
        private readonly HttpClient _client;
        private readonly Waiter _waiter;
        private readonly List<Task> _dataSaveTasks = new List<Task>();

        private ConcurrentBag<UserPrRequest> _allRequests = new ConcurrentBag<UserPrRequest>();

        public FetchUserPrList(IDataStore dataStore, HttpClient client, Waiter waiter)
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
            if (_allRequests.All(e => e.Fetched))
            {
                var updateStart = DateTimeOffset.UtcNow;

                var usersToUpdate = _dataStore.GetCollection<User>()
                                        .AsQueryable()
                                        .Where(u => u.Last_Update.Date < DateTimeOffset.UtcNow.AddDays(-2).Date || u.PR_Count != u.Items.Count)
                                        .ToList();

                var userPrRequests = usersToUpdate.Select(e => new UserPrRequest
                {
                    Login = e.Login,
                    GitHubId = e.GitHubId,
                    PR_Count = e.PR_Count,
                    Page = 1,
                    Last_Update = e.Last_Update
                }).ToList();

                _allRequests = new ConcurrentBag<UserPrRequest>(userPrRequests);
            }

            await GetPullRequests();
        }

        private async Task GetPullRequests()
        {
            _dataSaveTasks.Clear();
            var updateStart = DateTimeOffset.UtcNow;

            do
            {
                int skip = 0;

                var toFetch = _allRequests.Where(e => e.Fetched == false).OrderBy(e => e.Last_Update).ToList();

                Console.WriteLine($"UpdateUserPRList requests: {toFetch.Count}");

                while (toFetch.Any(e => e.Fetched == false))
                {
                    var updatedDatas = await HandleBatch(toFetch.Skip(skip).Take(Constants.BATCH_SIZE), updateStart);

                    var saveTask = SaveBatchToDataStore(updatedDatas);
                    _dataSaveTasks.Add(saveTask);

                    if (updatedDatas.Any(e => e == null))
                    {
                        await Task.WhenAll(_dataSaveTasks);
                        _dataSaveTasks.Clear();

                        throw new FetchFailedException();
                    }

                    skip += Constants.BATCH_SIZE;
                }
            } while (_allRequests.Any(e => e.Fetched == false));

            await Task.WhenAll(_dataSaveTasks);
            _dataSaveTasks.Clear();

            Console.WriteLine("UpdateUserPRList ready");
        }

        private async Task SaveBatchToDataStore(IEnumerable<UserPrRequest> datas)
        {
            var tasks = datas.Where(e => e != null)
                             .Select(userRequest =>
                             {
                                 return Task.Run(async () =>
                                 {
                                     var fromDb = _dataStore.GetCollection<User>().AsQueryable().First(e => e.GitHubId == userRequest.GitHubId);
                                     fromDb.Last_Update = userRequest.Last_Update;
                                     fromDb.PR_Count = userRequest.PR_Count;

                                     if (fromDb.PR_Count != fromDb.Items.Count)
                                     {
                                         foreach (var i in userRequest.Items)
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

        private async Task<IEnumerable<UserPrRequest>> HandleBatch(IEnumerable<UserPrRequest> usersBatch, DateTimeOffset updateStamp)
        {
            var tasks = usersBatch.Where(e => e.Fetched == false).Select(async user =>
            {
                //Console.WriteLine($"Request: {user.Login} - {user.Page}");
                try
                {
                    var response = await _client.GetAsync($"https://api.github.com/search/issues?per_page=100&q=author%3A{user.Login}+type%3Apr&page={user.Page}");

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        _waiter.RateLimitResetTime = response.Headers.SingleOrDefault(h => h.Key == Constants.RATE_LIMIT_HEADER).Value?.First() ?? "";
                        //Console.WriteLine($"Request fail: {user.Login} - {user.Page}");

                        return null;
                    }

                    //Console.WriteLine($"Request ok: {user.Login} - {user.Page}");

                    if (user.Page == 1 && response.Headers.Contains("Link"))
                    {
                        var links = response.Headers.SingleOrDefault(h => h.Key == "Link").Value;
                        var lastLink = links.First().Split(',').Last();
                        var start = lastLink.IndexOf("&page=") + 6;
                        var end = lastLink.IndexOf(">");
                        var lastPageNum = int.Parse(lastLink[start..end]);

                        // We are already requesting Page=1 so start from Page=2
                        var allUserRequests = Enumerable.Range(2, lastPageNum - 1).Select(idx => new UserPrRequest
                        {
                            Login = user.Login,
                            GitHubId = user.GitHubId,
                            Page = idx,
                            PR_Count = user.PR_Count
                        }).ToList();

                        foreach (var request in allUserRequests)
                            _allRequests.Add(request);
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    var resultObject = JsonConvert.DeserializeObject<PrResponse>(content);
                    user.PR_Count = resultObject.Total_Count;
                    user.Items = resultObject.Items;
                    user.Last_Update = updateStamp;
                    user.Fetched = true;
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

        private class UserPrRequest
        {
            public string Login { get; set; }
            public int Page { get; set; }
            public int GitHubId { get; set; }
            public int PR_Count { get; set; }
            public List<PullRequest> Items { get; set; } = new List<PullRequest>();
            public DateTimeOffset Last_Update { get; set; }
            public bool Fetched { get; set; }
        }
    }
}