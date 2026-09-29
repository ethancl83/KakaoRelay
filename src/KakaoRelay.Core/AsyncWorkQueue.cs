namespace KakaoRelay.Core;

// Reserve a place synchronously, then run one operation at a time in arrival order.
public sealed class AsyncWorkQueue
{
    private readonly object sync = new();
    private Task tail = Task.CompletedTask;

    public Task<T> Enqueue<T>(Func<Task<T>> operation)
    {
        Task previous;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            previous = tail;
            tail = finished.Task;
        }
        return RunAsync();

        async Task<T> RunAsync()
        {
            await previous;
            try { return await operation(); }
            finally { finished.SetResult(); }
        }
    }
}
