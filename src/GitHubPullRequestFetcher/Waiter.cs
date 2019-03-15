using System;

namespace GitHubPullRequestFetcher
{
    public class Waiter
    {
        private readonly DateTime _epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public string RateLimitReset { get; set; }

        public TimeSpan GetWaitTime()
        {
            var waitTime = string.IsNullOrEmpty(RateLimitReset)
                            ? TimeSpan.FromSeconds(10)
                            : FromUnixTime(RateLimitReset) - DateTime.UtcNow;

            Console.WriteLine($"Waiting for: {waitTime.TotalSeconds} secs");

            // Add 1 sec just in case. Sometimes, seems to fail when too close.
            return waitTime.TotalSeconds < 0 ? TimeSpan.FromSeconds(1) : waitTime.Add(TimeSpan.FromSeconds(1));
        }

        private DateTime FromUnixTime(string unixTime) => _epoch.AddSeconds(Convert.ToUInt64(unixTime));
    }
}