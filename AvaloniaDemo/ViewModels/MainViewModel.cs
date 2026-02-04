using AvaloniaDemo.Services;

namespace AvaloniaDemo.ViewModels;

public class MainViewModel
{
    public MainViewModel(ApiClient api, ILogger log)
    {
        Api = api;
        Log = log;

        log.Log("MainViewModel created");
    }

    public ApiClient Api { get; }

    public ILogger Log { get; }
}
