using System;
using Avalonia.Controls;
using AvaloniaDemo.ViewModels;

namespace AvaloniaDemo.Views;

public partial class DetailView : UserControl
{
    public DetailView(Func<Guid, DetailViewModel> viewModelFactory)
    {
        InitializeComponent();

        DataContext = viewModelFactory(Guid.NewGuid());
    }
}
