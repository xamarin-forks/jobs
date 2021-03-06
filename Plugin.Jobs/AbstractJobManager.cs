﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Plugin.Jobs.Infrastructure;
using Xamarin.Essentials;


namespace Plugin.Jobs
{
    public abstract class AbstractJobManager : IJobManager
    {
        protected AbstractJobManager(IJobRepository repository, IJobFactory factory)
        {
            this.Repository = repository ?? new SqliteJobRepository();
            this.Factory = factory ?? new ReflectionJobFactory();
        }


        protected IJobRepository Repository { get; }
        protected IJobFactory Factory { get; }


        public virtual async void RunTask(string taskName, Func<Task> task)
        {
            try
            {
                this.LogTask(JobState.Start, taskName);
                await task().ConfigureAwait(false);
                this.LogTask(JobState.Finish, taskName);
            }
            catch (Exception ex)
            {
                this.LogTask(JobState.Error, taskName, ex);
            }
        }


        public virtual async Task<JobRunResult> Run(string jobName, CancellationToken? cancelToken = null)
        {
            var job = this.Repository.GetByName(jobName);
            if (job == null)
                throw new ArgumentException("No job found named " + jobName);

            var result = await this.RunJob(job, "manual", cancelToken).ConfigureAwait(false);
            return result;
        }


        public virtual IEnumerable<JobInfo> GetJobs() => this.Repository.GetJobs();
        public virtual IEnumerable<JobLog> GetLogs(string jobName = null, DateTime? since = null, bool errorsOnly = false)
            => this.Repository.GetLogs(jobName, since, errorsOnly);


        public virtual void Cancel(string jobName) => this.Repository.Cancel(jobName);
        public virtual void CancelAll() => this.Repository.CancelAll();
        public bool IsRunning { get; protected set; }
        public event EventHandler<JobRunResult> JobFinished;


        public virtual Task Schedule(JobInfo jobInfo)
        {
            if (String.IsNullOrWhiteSpace(jobInfo.Name))
                throw new ArgumentException("No job name defined");

            if (jobInfo.Type == null)
                throw new ArgumentException("Type not set");

            //if (!jobInfo.Type.GetTypeInfo().IsAssignableFrom(typeof(IJob)))
            //    throw new ArgumentException($"{jobInfo.Type.FullName} is not an implementation of {typeof(IJob).Name}");

            var existing = this.Repository.GetByName(jobInfo.Name);
            if (existing == null)
            {
                this.Repository.Create(jobInfo);
            }
            else
            {
                existing.Type = jobInfo.Type;
                existing.BatteryNotLow = jobInfo.BatteryNotLow;
                existing.DeviceCharging = jobInfo.DeviceCharging;
                existing.RequiredNetwork = jobInfo.RequiredNetwork;
                existing.Parameters = jobInfo.Parameters;

                this.Repository.Update(existing);
            }
            return Task.CompletedTask;
        }


        public virtual Task<IEnumerable<JobRunResult>> RunAll(CancellationToken? cancelToken = null) => Task.Run(async () =>
        {
            if (this.IsRunning)
                throw new ArgumentException("Job manager is already running");

            var ct = cancelToken ?? CancellationToken.None;
            this.IsRunning = false;
            var jobs = this.Repository.GetJobs();
            var runId = Guid.NewGuid().ToString();
            var tasks = new List<Task<JobRunResult>>();

            foreach (var job in jobs)
            {
                if (this.CheckCriteria(job))
                    tasks.Add(this.RunJob(job, runId, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            var results = tasks.Select(x => x.Result).AsEnumerable();
            this.IsRunning = false;
            return results;
        });


        protected virtual bool CheckCriteria(JobInfo job)
        {
            var pluggedIn = Battery.State == BatteryState.Charging || Battery.State == BatteryState.Full;

            if (job.BatteryNotLow)
            {
                var lowBattery = Battery.ChargeLevel <= 0.2;
                if (!pluggedIn && lowBattery)
                    return false;
            }

            var inetAvail = Connectivity.NetworkAccess == NetworkAccess.Internet;
            var wifi = Connectivity.Profiles.Contains(ConnectionProfile.WiFi);
            if (job.RequiredNetwork == NetworkType.Any && !inetAvail)
                return false;

            if (job.RequiredNetwork == NetworkType.WiFi && !wifi)
                return false;

            return true;
        }


        protected virtual async Task<JobRunResult> RunJob(JobInfo job, string batchName, CancellationToken? cancelToken)
        {
            var ct = cancelToken ?? CancellationToken.None;
            var result = default(JobRunResult);
            try
            {
                this.LogJob(JobState.Start, job, "manual");
                var service = this.Factory.GetInstance(job);

                await service
                    .Run(job, ct)
                    .ConfigureAwait(false);

                this.LogJob(JobState.Finish, job, "manual");
                result = new JobRunResult(job, null);
            }
            catch (Exception ex)
            {
                this.LogJob(JobState.Error, job, "manual", ex);
                result = new JobRunResult(job, ex);
            }
            finally
            {
                job.LastRunUtc = DateTime.UtcNow;
                this.Repository.Update(job);
            }
            this.JobFinished?.Invoke(this, result);
            return result;
        }

        protected virtual void LogJob(JobState state,
                                      JobInfo job,
                                      string runId,
                                      Exception exception = null)
            => this.Repository.Log(new JobLog
            {
                JobName = job.Name,
                RunId = runId,
                CreatedOn = DateTime.UtcNow,
                Status = state,
                Error = exception?.ToString() ?? String.Empty
            });


        protected virtual void LogTask(JobState state, string taskName, Exception exception = null)
            => this.Repository.Log(new JobLog
            {
                JobName = taskName,
                CreatedOn = DateTime.UtcNow,
                Status = state,
                Error = exception?.ToString() ?? String.Empty
            });
    }
}
