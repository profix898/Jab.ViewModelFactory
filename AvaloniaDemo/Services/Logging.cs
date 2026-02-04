using System;

namespace AvaloniaDemo.Services;

public interface ILogger
{
    void Log(string message);
}

public sealed class ConsoleLogger : ILogger
{
    #region Implementation of ILogger

    public void Log(string message)
    {
        Console.WriteLine(message);
    }

    #endregion
}
