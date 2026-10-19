using L2Mod.Core.Updates;

namespace L2Mod.Core.Tests.Updates;

/// <summary>The real named mutex. Names are unique per test, so a running launcher is never affected.</summary>
public class InstanceLockTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly NamedMutexInstanceLockProvider _provider = new NamedMutexInstanceLockProvider();
    private readonly string _name = $@"Local\L2Mod.Tests.{Guid.NewGuid():N}";

    [Fact]
    public void SecondInstance_GetsNoLockWhileTheFirstRuns()
    {
        using IInstanceLock? first = _provider.TryAcquire(_name, TimeSpan.Zero);
        Assert.NotNull(first);
        Assert.True(first.IsHeld);

        IInstanceLock? second = _provider.TryAcquire(_name, TimeSpan.Zero);

        Assert.Null(second);
    }

    [Fact]
    public async Task RestartedInstance_WaitsUntilThePreviousOneReleases()
    {
        IInstanceLock? previous = _provider.TryAcquire(_name, TimeSpan.Zero);
        Assert.NotNull(previous);
        TimeSpan timeout = UpdateStartupInfo.Parse(new[] { "--updated-from", "1.0.0" }).InstanceLockTimeout;

        Task<IInstanceLock?> restarted = Task.Run(() => _provider.TryAcquire(_name, timeout));
        await Task.Delay(200);
        Assert.False(restarted.IsCompleted);
        previous.Dispose();

        using IInstanceLock? acquired = await restarted.WaitAsync(Patience);
        Assert.NotNull(acquired);
        Assert.True(acquired.IsHeld);
    }

    [Fact]
    public async Task Dispose_WorksFromAnotherThread()
    {
        IInstanceLock? held = _provider.TryAcquire(_name, TimeSpan.Zero);
        Assert.NotNull(held);

        await Task.Run(() => held.Dispose()).WaitAsync(Patience);
        held.Dispose();

        Assert.False(held.IsHeld);
        using IInstanceLock? next = _provider.TryAcquire(_name, TimeSpan.Zero);
        Assert.NotNull(next);
    }

    [Fact]
    public void LockOfACrashedLauncher_IsTakenOver()
    {
        // A thread that ends while owning the mutex leaves it abandoned, like a killed process.
        var crashed = new Thread(() =>
        {
            var mutex = new Mutex(false, _name);
            mutex.WaitOne();
        });
        crashed.Start();
        Assert.True(crashed.Join(Patience));

        using IInstanceLock? next = _provider.TryAcquire(_name, TimeSpan.Zero);

        Assert.NotNull(next);
        Assert.True(next.IsHeld);
    }

    [Fact]
    public void InvalidName_Throws()
    {
        // Only Global\ and Local\ exist; the error comes from the owner thread and must not crash the process.
        Assert.ThrowsAny<Exception>(() => _provider.TryAcquire($@"NoSuchNamespace\{Guid.NewGuid():N}", TimeSpan.Zero));
    }
}
