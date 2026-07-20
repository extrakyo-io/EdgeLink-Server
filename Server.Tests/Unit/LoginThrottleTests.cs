using EdgeLink.WebApi;
using Xunit;

namespace EdgeLink.Tests.Unit;

/// <summary>#11:登入速率限制。先前完全沒有限制,而每次驗證要跑 100,000 次 PBKDF2,
/// 既能無限猜密碼,也是不對稱的 CPU 放大攻擊面。</summary>
public class LoginThrottleTests
{
    private static LoginThrottle Fresh()
    {
        LoginThrottle.Instance.Reset();
        return LoginThrottle.Instance;
    }

    [Fact]
    public void NotLockedOut_Initially()
    {
        var t = Fresh();
        Assert.False(t.IsLockedOut("1.2.3.4", out _));
    }

    [Fact]
    public void FewFailures_DoNotLockOut()
    {
        var t = Fresh();
        for (int i = 0; i < 4; i++) t.RecordFailure("1.2.3.4");
        Assert.False(t.IsLockedOut("1.2.3.4", out _));
    }

    [Fact]
    public void FifthFailure_LocksOutWithRetryAfter()
    {
        var t = Fresh();
        for (int i = 0; i < 5; i++) t.RecordFailure("1.2.3.4");

        Assert.True(t.IsLockedOut("1.2.3.4", out int retryAfter));
        Assert.InRange(retryAfter, 1, 60);
    }

    [Fact]
    public void FurtherFailures_ExtendLockout()
    {
        var t = Fresh();
        for (int i = 0; i < 5; i++) t.RecordFailure("1.2.3.4");
        t.IsLockedOut("1.2.3.4", out int first);
        for (int i = 0; i < 3; i++) t.RecordFailure("1.2.3.4");
        t.IsLockedOut("1.2.3.4", out int later);

        Assert.True(later > first, $"鎖定時間應遞增,但 {later} <= {first}");
    }

    [Fact]
    public void Lockout_IsPerClient()
    {
        var t = Fresh();
        for (int i = 0; i < 6; i++) t.RecordFailure("1.2.3.4");

        Assert.True(t.IsLockedOut("1.2.3.4", out _));
        Assert.False(t.IsLockedOut("5.6.7.8", out _));   // 不能連累其他來源
    }

    [Fact]
    public void Success_ClearsFailures()
    {
        var t = Fresh();
        for (int i = 0; i < 4; i++) t.RecordFailure("1.2.3.4");
        t.RecordSuccess("1.2.3.4");
        for (int i = 0; i < 4; i++) t.RecordFailure("1.2.3.4");

        Assert.False(t.IsLockedOut("1.2.3.4", out _));   // 計數已歸零,4 次不該鎖
    }
}
