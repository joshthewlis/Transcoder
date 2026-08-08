using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Options;

namespace Transcoder.Server.Services;

public sealed class WorkerMonitorService(IServiceProvider services, IOptions<WorkerTimingOptions> timingOptions, ILogger<WorkerMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TranscoderDbContext>();
                await UpdateWorkersAndRequeueLostJobsAsync(db, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker monitor failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task UpdateWorkersAndRequeueLostJobsAsync(TranscoderDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var timing = timingOptions.Value;
        var workers = await db.Workers.ToListAsync(cancellationToken);

        foreach (var worker in workers)
        {
            var previousState = worker.State;

            if (worker.ControlState == WorkerControlState.Disabled)
            {
                worker.State = WorkerState.Disabled;
                if (previousState != worker.State)
                {
                    logger.LogInformation(
                        "Worker state changed: Worker={WorkerId}; {PreviousState} -> {WorkerState}; Control={ControlState}",
                        worker.WorkerId,
                        previousState,
                        worker.State,
                        worker.ControlState);
                }
                continue;
            }

            if (worker.State is WorkerState.RequirementsFailed or WorkerState.PathCheckRequired or WorkerState.PathCheckFailed)
                continue;

            if (worker.LastSeenUtc is null)
                continue;

            var age = now - worker.LastSeenUtc.Value;
            worker.State = age.TotalSeconds switch
            {
                var seconds when seconds > timing.LostAfterSeconds => WorkerState.Lost,
                var seconds when seconds > timing.UnresponsiveAfterSeconds => WorkerState.Unresponsive,
                _ when worker.ControlState != WorkerControlState.Normal => WorkerState.Draining,
                _ => WorkerState.Online
            };

            if (worker.State != previousState)
            {
                if (worker.State == WorkerState.Lost)
                {
                    logger.LogWarning(
                        "Worker disconnected/lost: Worker={WorkerId}; Name={WorkerName}; LastSeenUtc={LastSeenUtc}; AgeSeconds={AgeSeconds:F0}",
                        worker.WorkerId,
                        worker.WorkerName,
                        worker.LastSeenUtc,
                        age.TotalSeconds);
                }
                else if (worker.State == WorkerState.Unresponsive)
                {
                    logger.LogWarning(
                        "Worker unresponsive: Worker={WorkerId}; Name={WorkerName}; LastSeenUtc={LastSeenUtc}; AgeSeconds={AgeSeconds:F0}",
                        worker.WorkerId,
                        worker.WorkerName,
                        worker.LastSeenUtc,
                        age.TotalSeconds);
                }
                else
                {
                    logger.LogInformation(
                        "Worker state changed: Worker={WorkerId}; {PreviousState} -> {WorkerState}; AgeSeconds={AgeSeconds:F0}; Control={ControlState}",
                        worker.WorkerId,
                        previousState,
                        worker.State,
                        age.TotalSeconds,
                        worker.ControlState);
                }
            }
        }

        var expiredJobs = await db.Jobs
            .Where(x => (x.Status == JobStatus.Leased || x.Status == JobStatus.Running) && x.LeaseExpiresUtc != null && x.LeaseExpiresUtc < now)
            .ToListAsync(cancellationToken);

        foreach (var job in expiredJobs)
        {
            var expiredWorkerId = job.LeasedByWorkerId;
            var expiredLeaseId = job.LeaseId;

            job.LastError = "Worker lease expired; job requeued.";
            job.LastLeaseId = job.LeaseId;
            job.LeaseId = null;
            job.LeasedByWorkerId = null;
            job.LeasedByWorkerInstanceId = null;
            job.LeaseStartedUtc = null;
            job.LeaseLastSeenUtc = null;
            job.LeaseExpiresUtc = null;
            job.Progress = null;
            job.Status = job.AttemptNumber >= job.MaxAttempts ? JobStatus.Failed : JobStatus.Queued;

            logger.LogWarning(
                "Job lease expired: JobId={JobId}; Type={JobType}; Worker={WorkerId}; LeaseId={LeaseId}; Attempt={AttemptNumber}/{MaxAttempts}; NewStatus={NewStatus}",
                job.Id,
                job.JobType,
                expiredWorkerId,
                expiredLeaseId,
                job.AttemptNumber,
                job.MaxAttempts,
                job.Status);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
