using System.Collections.Concurrent;

namespace MaskFlow.Api.Infrastructure.Security;

public sealed class LoginRateLimiter
{
    const int SweepThreshold = 4096;
    readonly ConcurrentDictionary<string, AttemptWindow> windows = new();
    readonly Func<DateTimeOffset> clock;

    public LoginRateLimiter() : this(() => DateTimeOffset.UtcNow) { }

    public LoginRateLimiter(Func<DateTimeOffset> clock)
    {
        this.clock = clock;
    }

    public static int MaxAttempts =>
        int.TryParse(Environment.GetEnvironmentVariable("MASKFLOW_LOGIN_MAX_ATTEMPTS"), out var value) && value > 0
            ? value
            : 5;

    public static int MaxAttemptsPerIp =>
        int.TryParse(Environment.GetEnvironmentVariable("MASKFLOW_LOGIN_MAX_ATTEMPTS_PER_IP"), out var value) && value > 0
            ? value
            : 30;

    static TimeSpan LockoutDuration
    {
        get
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("MASKFLOW_LOGIN_LOCKOUT_MINUTES"), out var minutes) && minutes > 0)
            {
                return TimeSpan.FromMinutes(minutes);
            }

            return TimeSpan.FromMinutes(15);
        }
    }

    public int TrackedKeys => windows.Count;

    public bool IsLocked(string key, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (!windows.TryGetValue(key, out var window))
        {
            return false;
        }

        var now = clock();
        if (window.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((lockedUntil - now).TotalSeconds));
            return true;
        }

        if (IsExpired(window, now))
        {
            windows.TryRemove(key, out _);
        }

        return false;
    }

    public void RecordFailure(string key, int? maxAttempts = null)
    {
        var now = clock();
        var limit = maxAttempts ?? MaxAttempts;
        windows.AddOrUpdate(
            key,
            _ => new AttemptWindow(1, now, limit <= 1 ? now + LockoutDuration : null),
            (_, current) =>
            {
                var expired = current.FirstFailureAt + LockoutDuration < now;
                var failures = expired ? 1 : current.Failures + 1;
                var firstFailureAt = expired ? now : current.FirstFailureAt;
                var lockedUntil = failures >= limit ? now + LockoutDuration : (DateTimeOffset?)null;
                return new AttemptWindow(failures, firstFailureAt, lockedUntil);
            });

        if (windows.Count > SweepThreshold)
        {
            Sweep(now);
        }
    }

    public void RecordSuccess(string key) => windows.TryRemove(key, out _);

    // Unique keys (IP + email) would otherwise accumulate forever.
    void Sweep(DateTimeOffset now)
    {
        foreach (var (key, window) in windows)
        {
            if (IsExpired(window, now))
            {
                windows.TryRemove(key, out _);
            }
        }
    }

    static bool IsExpired(AttemptWindow window, DateTimeOffset now) =>
        (window.LockedUntil is null || window.LockedUntil <= now)
        && window.FirstFailureAt + LockoutDuration < now;

    sealed record AttemptWindow(int Failures, DateTimeOffset FirstFailureAt, DateTimeOffset? LockedUntil);
}
