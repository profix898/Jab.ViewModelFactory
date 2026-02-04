using System;
using AvaloniaDemo.Services;

namespace AvaloniaDemo.ViewModels;

public class DetailViewModel
{
    // View provides Guid, other parameters are injected via DI
    public DetailViewModel(Guid id, ApiClient api, ILogger log)
    {
        Id = id;
        Api = api;
        Log = log;

        log.Log($"DetailViewModel created for {id}");
    }

    public ApiClient Api { get; }

    public Guid Id { get; }

    public ILogger Log { get; }
}
