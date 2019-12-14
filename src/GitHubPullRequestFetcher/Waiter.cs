using Serilog;
using System;

namespace GitHubPullRequestFetcher
{
    public class Waiter
    {
        private readonly DateTime _epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public string RateLimitResetTime { get; set; } = string.Empty;

        public TimeSpan GetWaitTime()
        {
            var waitTime = string.IsNullOrEmpty(RateLimitResetTime)
                            ? TimeSpan.FromSeconds(10)
                            : FromUnixTime(RateLimitResetTime) - DateTime.UtcNow;

            Log.Information($"Waiting for: {waitTime.TotalSeconds} secs");

            // Add 1 sec just in case. Sometimes, seems to fail when too close.
            return waitTime.TotalSeconds < 0 ? TimeSpan.FromSeconds(1) : waitTime.Add(TimeSpan.FromSeconds(1));
        }

        private DateTime FromUnixTime(string unixTime) => _epoch.AddSeconds(double.Parse(unixTime));
    }
}