using System.Runtime.ExceptionServices;

namespace L2Mod.Core.Updates;

/// <summary>Proof that this process is the only launcher instance. Held for the whole life of the launcher.</summary>
public interface IInstanceLock : IDisposable
{
    /// <summary>False once disposed.</summary>
    bool IsHeld { get; }
}

/// <summary>Acquires the single-instance lock; faked in tests.</summary>
public interface IInstanceLockProvider
{
    /// <summary>The lock, or null when another instance still holds it after <paramref name="timeout"/>.</summary>
    IInstanceLock? TryAcquire(string name, TimeSpan timeout);
}

/// <summary>
/// Single-instance lock as a named mutex. Two launchers at once would race on the update side files, on the
/// state file and on Icon.utx, so the second one gets null and should tell the user and exit. A launcher started
/// by another one (update, rollback, see <see cref="UpdateStartupInfo.InstanceLockTimeout"/>) waits, because the
/// starting process holds the lock until it exits. The lock of a crashed or killed launcher counts as free.
/// </summary>
/// <remarks>
/// A mutex belongs to the thread that acquired it, which does not mix with async code, so each lock owns a small
/// dedicated thread: <see cref="IDisposable.Dispose"/> works from any thread. If the process ends without
/// disposing, the thread dies with it and the next launcher takes the abandoned mutex.
/// </remarks>
public sealed class NamedMutexInstanceLockProvider : IInstanceLockProvider
{
    /// <summary>Per Windows session, like the data directory in %LocalAppData%.</summary>
    public const string DefaultName = @"Local\CpLauncher.SingleInstance";

    public IInstanceLock? TryAcquire(string name, TimeSpan timeout)
    {
        var instanceLock = new MutexInstanceLock(name, timeout);
        if (instanceLock.IsHeld)
        {
            return instanceLock;
        }

        instanceLock.Dispose();
        return null;
    }

    private sealed class MutexInstanceLock : IInstanceLock
    {
        private const int OwnerStackSize = 256 * 1024;

        private readonly ManualResetEventSlim _decided = new ManualResetEventSlim();
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim();
        private readonly Thread _owner;
        private Exception? _error;
        private volatile bool _held;
        private int _disposed;

        public MutexInstanceLock(string name, TimeSpan timeout)
        {
            _owner = new Thread(() => Own(name, timeout), OwnerStackSize)
            {
                IsBackground = true,
                Name = "Launcher instance lock",
            };
            _owner.Start();
            _decided.Wait();
            if (_error is not null)
            {
                _owner.Join();
                ExceptionDispatchInfo.Throw(_error);
            }
        }

        public bool IsHeld
        {
            get
            {
                return _held;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _held = false;
            _release.Set();
            _owner.Join();
            _decided.Dispose();
            _release.Dispose();
        }

        private void Own(string name, TimeSpan timeout)
        {
            Mutex mutex;
            try
            {
                mutex = new Mutex(false, name);
            }
            catch (Exception error)
            {
                _error = error;
                _decided.Set();
                return;
            }

            using (mutex)
            {
                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                catch (Exception error)
                {
                    _error = error;
                    _decided.Set();
                    return;
                }

                _held = acquired;
                _decided.Set();
                if (!acquired)
                {
                    return;
                }

                _release.Wait();
                mutex.ReleaseMutex();
            }
        }
    }
}
