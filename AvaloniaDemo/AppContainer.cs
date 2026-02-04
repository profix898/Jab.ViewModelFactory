using System;
using AvaloniaDemo.Services;
using AvaloniaDemo.ViewModels;
using AvaloniaDemo.Views;
using Jab;

namespace AvaloniaDemo;

[ServiceProvider]
[Singleton<ApiClient>]
[Singleton<ILogger, ConsoleLogger>]
[Transient<DetailView>]
public partial class AppContainer
{
}
