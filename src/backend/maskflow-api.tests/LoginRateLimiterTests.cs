using MaskFlow.Api.Infrastructure.Security;

namespace MaskFlow.Api.Tests;

public class LoginRateLimiterTests
{
    [Fact]
    public void LocksAfterMaxAttemptsAndUnlocksAfterWindow()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var limiter = new LoginRateLimiter(() => now);

        for (var i = 0; i < 4; i++) limiter.RecordFailure("ip:a@b.c", maxAttempts: 5);
        Assert.False(limiter.IsLocked("ip:a@b.c", out _));

        limiter.RecordFailure("ip:a@b.c", maxAttempts: 5);
        Assert.True(limiter.IsLocked("ip:a@b.c", out var retry));
        Assert.InRange(retry, 1, 15 * 60);

        now = now.AddMinutes(16);
        Assert.False(limiter.IsLocked("ip:a@b.c", out _));
    }

    [Fact]
    public void SeparateKeysDoNotAffectEachOther()
    {
        var limiter = new LoginRateLimiter();
        for (var i = 0; i < 5; i++) limiter.RecordFailure("1.1.1.1:victim@example.com", maxAttempts: 5);

        Assert.True(limiter.IsLocked("1.1.1.1:victim@example.com", out _));
        Assert.False(limiter.IsLocked("2.2.2.2:victim@example.com", out _));
    }

    [Fact]
    public void SweepsExpiredWindows()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var limiter = new LoginRateLimiter(() => now);
        for (var i = 0; i < 5000; i++) limiter.RecordFailure($"k{i}", maxAttempts: 100);

        now = now.AddHours(1);
        limiter.RecordFailure("fresh", maxAttempts: 100);

        Assert.Equal(1, limiter.TrackedKeys);
    }
}
