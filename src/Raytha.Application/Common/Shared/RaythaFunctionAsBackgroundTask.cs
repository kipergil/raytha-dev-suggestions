﻿using Raytha.Application.Common.Exceptions;
using Raytha.Application.Common.Interfaces;
using Raytha.Domain.Entities;
using System.Text.Json;

namespace Raytha.Application.Common.Shared;

public class RaythaFunctionAsBackgroundTaskPayload
{
    public dynamic Target { get; init; }
    public RaythaFunction RaythaFunction { get; init; } 
}

public class RaythaFunctionAsBackgroundTask : IExecuteBackgroundTask 
{
    private readonly IRaythaDbContext _entityFrameworkDb;
    private readonly IRaythaFunctionConfiguration _raythaFunctionConfiguration;
    private readonly IRaythaFunctionScriptEngine _raythaFunctionScriptEngine;
    private readonly IRaythaFunctionSemaphore _raythaFunctionSemaphore;

    public RaythaFunctionAsBackgroundTask(
        IRaythaFunctionConfiguration raythaFunctionConfiguration,
        IRaythaFunctionSemaphore raythaFunctionSemaphore,
        IRaythaFunctionScriptEngine raythaFunctionScriptEngine,
        IRaythaDbContext entityFrameworkDb)
    {
        _raythaFunctionConfiguration = raythaFunctionConfiguration;
        _raythaFunctionSemaphore = raythaFunctionSemaphore;
        _raythaFunctionScriptEngine = raythaFunctionScriptEngine;
        _entityFrameworkDb = entityFrameworkDb;
    }

    public async Task Execute(Guid jobId, JsonElement args, CancellationToken cancellationToken)
    {
        string? raythaFunctionName = args.GetProperty("RaythaFunction").GetProperty("Name").GetString();
        string? code = args.GetProperty("RaythaFunction").GetProperty("Code").GetString();

        var job = _entityFrameworkDb.BackgroundTasks.First(p => p.Id == jobId);
        job.TaskStep = 1;
        job.StatusInfo = $"Running Raytha Function";
        job.PercentComplete = 0;
        _entityFrameworkDb.BackgroundTasks.Update(job);
        await _entityFrameworkDb.SaveChangesAsync(cancellationToken);

        if (await _raythaFunctionSemaphore.WaitAsync(_raythaFunctionConfiguration.QueueTimeout, cancellationToken))
        {
            try
            {
                string payload = JsonSerializer.Serialize(args.GetProperty("Target"));
                await _raythaFunctionScriptEngine.EvaluateRun(code, payload, _raythaFunctionConfiguration.ExecuteTimeout, cancellationToken);
                job.TaskStep = 2;
                job.StatusInfo = $"Completed Raytha Function: {raythaFunctionName}";
                job.PercentComplete = 100;
            }
            catch (Exception exception) when(exception is RaythaFunctionExecuteTimeoutException or RaythaFunctionScriptException)
            {
                job.StatusInfo = $"Error running Raytha Function {raythaFunctionName} - {exception.Message}";
            }
            finally
            {
                job.TaskStep = 2;
                job.PercentComplete = 100;
                _entityFrameworkDb.BackgroundTasks.Update(job);
                await _entityFrameworkDb.SaveChangesAsync(cancellationToken);
                _raythaFunctionSemaphore.Release();
            }
        }
        else
        {
            job.TaskStep = 2;
            job.StatusInfo = $"Raytha Function {raythaFunctionName} failed to run because too many background tasks are running";
            job.PercentComplete = 100;
            _entityFrameworkDb.BackgroundTasks.Update(job);
            await _entityFrameworkDb.SaveChangesAsync(cancellationToken);
        }
    }
}
