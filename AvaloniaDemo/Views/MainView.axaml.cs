using System;
using Avalonia.Controls;
using AvaloniaDemo.ViewModels;

namespace AvaloniaDemo.Views;

public partial class MainView : UserControl
{
    public MainView(Func<MainViewModel> viewModelFactory)
    {
        InitializeComponent();

        DataContext = viewModelFactory();
    }
}
