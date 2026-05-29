using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;

namespace VisualRelay.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var viewModel = new MainWindowViewModel();
            viewModel.UseFolderPicker(new AvaloniaFolderPicker(window));
            window.DataContext = viewModel;
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
