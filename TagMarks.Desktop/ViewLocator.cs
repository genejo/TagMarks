using System;
using System.Collections.Generic;
using TagMarks.Desktop.ViewModels;
using TagMarks.Desktop.Views;
using ReactiveUI.Binding.Reactive;

namespace TagMarks.Desktop;

/// <summary>
/// Scaffolding for future ReactiveUI routing support.
/// Currently unused — the single MainWindowView is created directly in App.axaml.cs.
/// When multi-view navigation is added, this locator will resolve Views from ViewModels.
/// </summary>
public class ViewLocator : IViewLocator
{
    private readonly Dictionary<Type, Func<IViewFor>> _map = new()
    {
        [typeof(MainWindowViewModel)] = () => new MainWindowView(),
    };

    public IViewFor? ResolveView<TViewModel>(TViewModel viewModel, string? contract = null)
        where TViewModel : class
    {
        if (viewModel is null) return null;
        return _map.TryGetValue(viewModel.GetType(), out var factory) ? factory() : null;
    }

    public IViewFor? ResolveView(object? viewModel, string? contract = null)
    {
        if (viewModel is null) return null;
        return _map.TryGetValue(viewModel.GetType(), out var factory) ? factory() : null;
    }

    public IViewFor? ResolveViewUnsafe(object? viewModel, string? contract = null) =>
        ResolveView(viewModel, contract);
}
