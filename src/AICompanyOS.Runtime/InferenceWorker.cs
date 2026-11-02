namespace AICompanyOS.Runtime;

public sealed class InferenceWorker(FileEventStore store, ILocalModelProvider provider, ILogger<InferenceWorker> logger) : BackgroundService
{
    public string WorkerId { get; } = "worker-" + Guid.NewGuid().ToString("N");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.RecoverModelJobsAsync(WorkerId, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOneAsync(stoppingToken)) await Task.Delay(500, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                logger.LogError("Local model worker could not advance; persisted state remains authoritative.");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
    public async Task<bool> RunOneAsync(CancellationToken ct)
    {
        var job = await store.ClaimModelJobAsync(WorkerId, DateTimeOffset.UtcNow, ct);
        if (job is null) return false;
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        invocation.CancelAfter(TimeSpan.FromSeconds(120));
        ModelResult? result = null; string? error = null;
        Task<ModelResult>? generation = null;
        try
        {
            if (!await store.HeartbeatModelJobAsync(job, invocation.Token)) return true;
            generation = provider.GenerateAsync(job, invocation.Token);
            while (!generation.IsCompleted)
            {
                var pulse = Task.Delay(TimeSpan.FromSeconds(5), invocation.Token);
                if (await Task.WhenAny(generation, pulse) == generation) break;
                await pulse;
                if (!await store.HeartbeatModelJobAsync(job, invocation.Token)) { invocation.Cancel(); break; }
            }
            result = await generation.WaitAsync(invocation.Token);
        }
        catch (ModelProviderException failure) { error = failure.Code; }
        catch (OperationCanceledException) { error = ct.IsCancellationRequested ? "RUNTIME_STOPPED" : "MODEL_CANCELLED_OR_TIMED_OUT"; }
        catch (Exception) { error = "MODEL_PROVIDER_FAILED"; }
        invocation.Cancel();
        if (generation is not null)
            _ = generation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (ct.IsCancellationRequested) return true; // Restart recovery reconciles the persisted lease, never blind re-execution.
        await store.FinishModelJobAsync(job, result, error, ct);
        return true;
    }
}
