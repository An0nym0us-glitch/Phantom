using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Phantom.App.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);
}

/// <summary>Base class for the top-level pages hosted by the shell.</summary>
public abstract class PageViewModel : ObservableObject
{
    private bool _isActive;

    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    /// <summary>Glyph from the Segoe Fluent / MDL2 icon font.</summary>
    public abstract string Icon { get; }

    /// <summary>True while the page is visible. Hidden pages skip expensive per-frame work.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (Set(ref _isActive, value))
            {
                if (value)
                {
                    OnActivated();
                }
                else
                {
                    OnDeactivated();
                }
            }
        }
    }

    protected virtual void OnActivated()
    {
    }

    protected virtual void OnDeactivated()
    {
    }
}
