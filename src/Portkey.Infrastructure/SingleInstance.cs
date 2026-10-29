namespace Portkey.Infrastructure;

// A crashed owner abandons the named mutex. Constructing with initiallyOwned:true
// throws AbandonedMutexException before the caller receives a handle, so the next
// start would crash again and never recover. WaitOne reports abandonment while
// keeping this handle.
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    public bool IsFirst { get; }

    public SingleInstance(string name)
    {
        mutex = new Mutex(false, name);
        try { IsFirst = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsFirst = true; }
    }

    public void Dispose()
    {
        try { if (IsFirst) mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        mutex.Dispose();
    }
}
