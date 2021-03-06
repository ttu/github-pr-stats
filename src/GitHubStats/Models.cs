﻿using System;
using System.Collections.Generic;

namespace GitHubStats
{
    public class FetchFailedException : Exception { }

    public class UsersRequest
    {
        public int Page { get; set; }
        public List<User> Items { get; set; } = new List<User>();
        public bool Fetched { get; set; }
    }

    public class UserResponseResult
    {
        public int Total_Count { get; set; }
        public List<User> Items { get; set; } = new List<User>();
    }

    public class PrResponse
    {
        public string Id { get; set; }
        public int Total_Count { get; set; }
        public List<PullRequest> Items { get; set; } = new List<PullRequest>();
    }

    public interface IUserPrData
    {
        public int GitHubId { get; set; }
        public int PR_Count { get; set; }
        public List<PullRequest> Items { get; set; }
        public DateTimeOffset Last_Update { get; set; }
    }

    public class User : IUserPrData
    {
        public int Id { get; set; }
        public int GitHubId { get; set; }
        public string Login { get; set; }
        public string Html_Url { get; set; }
        public int PR_Count { get; set; }
        public List<PullRequest> Items { get; set; } = new List<PullRequest>();
        public DateTimeOffset Last_Update { get; set; }
    }

    public class PullRequest
    {
        public int Id { get; set; }
        public string Repository_Url { get; set; }
        public string Author_Association { get; set; }
        public string State { get; set; }
    }

    public class UserStats
    {
        public string Login { get; set; }
        public string Html_Url { get; set; }
        public int PR_Count { get; set; }
    }

    public class Repository
    {
        public string Repository_Url { get; set; }
        public DateTimeOffset Last_Update { get; set; }
        public Dictionary<string, long> Languages { get; set; } = new Dictionary<string, long>();
    }
}