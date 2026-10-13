namespace ATM.Application.Jobs;

/// <summary>When a job is due: every fixed interval, or once a day at a UTC time of day (cron-style, without the cron syntax).</summary>
public abstract record JobSchedule
{
    public static JobSchedule Every(TimeSpan interval) => new IntervalSchedule(interval);

    public static JobSchedule DailyAtUtc(TimeOnly time) => new DailySchedule(time);

    public abstract DateTimeOffset NextAfter(DateTimeOffset last);
}

public sealed record IntervalSchedule(TimeSpan Interval) : JobSchedule
{
    public override DateTimeOffset NextAfter(DateTimeOffset last) => last + Interval;
}

public sealed record DailySchedule(TimeOnly Time) : JobSchedule
{
    public override DateTimeOffset NextAfter(DateTimeOffset last)
    {
        var candidate = new DateTimeOffset(last.UtcDateTime.Date, TimeSpan.Zero) + Time.ToTimeSpan();
        return candidate > last ? candidate : candidate.AddDays(1);
    }
}

public interface IScheduledJob
{
    string Name { get; }

    JobSchedule Schedule { get; }

    Task RunAsync(CancellationToken cancellationToken);
}

public sealed record JobStatus(string Name, DateTimeOffset? LastRunUtc, DateTimeOffset NextRunUtc, bool Running, string? LastError, int RunCount);

/// <summary>
/// Decides which jobs are due and runs them. A job never overlaps with itself, one job's failure does not affect others,
/// and shutdown waits for running jobs (or cancels them after the grace period).
/// </summary>
public sealed class JobRunner(IEnumerable<IScheduledJob> jobs, TimeProvider clock)
{
    private readonly Dictionary<string, State> states = jobs.ToDictionary(j => j.Name, j => new State(j, clock.GetUtcNow()), StringComparer.Ordinal);

    public IReadOnlyList<JobStatus> Status() =>
        [.. states.Values.Select(s => new JobStatus(s.Job.Name, s.LastRun, s.Next, s.Running, s.LastError, s.RunCount)).OrderBy(s => s.Name, StringComparer.Ordinal)];

    /// <summary>Starts every due job that is not already running and returns the started tasks.</summary>
    public IReadOnlyList<Task> StartDueJobs(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var started = new List<Task>();
        foreach (var state in states.Values)
        {
            lock (state)
            {
                if (state.Running || now < state.Next)
                {
                    continue;
                }

                state.Running = true;
            }

            started.Add(Execute(state, cancellationToken));
        }

        return started;
    }

    public async Task RunLoopAsync(TimeSpan tick, CancellationToken stopToken, TimeSpan gracePeriod)
    {
        var running = new List<Task>();
        try
        {
            using var timer = new PeriodicTimer(tick);
            do
            {
                running.RemoveAll(t => t.IsCompleted);
                running.AddRange(StartDueJobs(stopToken));
            }
            while (await timer.WaitForNextTickAsync(stopToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // stopping
        }

        await Task.WhenAll(running).WaitAsync(gracePeriod).ConfigureAwait(false);
    }

    private async Task Execute(State state, CancellationToken cancellationToken)
    {
        try
        {
            await state.Job.RunAsync(cancellationToken).ConfigureAwait(false);
            state.LastError = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.LastError = "cancelled";
        }
        catch (Exception ex)
        {
            state.LastError = ex.Message;
        }
        finally
        {
            var finished = clock.GetUtcNow();
            state.LastRun = finished;
            state.Next = state.Job.Schedule.NextAfter(finished);
            state.RunCount++;
            lock (state)
            {
                state.Running = false;
            }
        }
    }

    private sealed class State(IScheduledJob job, DateTimeOffset created)
    {
        public IScheduledJob Job { get; } = job;

        public bool Running { get; set; }

        public DateTimeOffset? LastRun { get; set; }

        public DateTimeOffset Next { get; set; } = job.Schedule is IntervalSchedule i ? created + i.Interval : job.Schedule.NextAfter(created);

        public string? LastError { get; set; }

        public int RunCount { get; set; }
    }
}

/// <summary>Restarts the client when the UI stops signalling. The restart itself is an Infrastructure concern.</summary>
public interface IClientRestarter
{
    void Restart(string reason);
}

public sealed class Watchdog(IClientRestarter restarter, TimeProvider clock, TimeSpan timeout)
{
    private long lastBeatTicks = clock.GetUtcNow().UtcTicks;
    private int fired;

    /// <summary>Called by the UI thread on a timer. If the UI is hung, this stops being called.</summary>
    public void Beat() => Interlocked.Exchange(ref lastBeatTicks, clock.GetUtcNow().UtcTicks);

    public bool Check()
    {
        var silent = clock.GetUtcNow() - new DateTimeOffset(Interlocked.Read(ref lastBeatTicks), TimeSpan.Zero);
        if (silent < timeout || Interlocked.Exchange(ref fired, 1) == 1)
        {
            return false;
        }

        restarter.Restart($"UI heartbeat stopped for {silent.TotalSeconds:0} seconds");
        return true;
    }
}
