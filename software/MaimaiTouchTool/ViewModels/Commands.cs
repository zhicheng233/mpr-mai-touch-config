using System.Windows.Input;

namespace MaimaiTouchTool.ViewModels;

/// <summary>同步命令：用于 +/- 步进、批量调整这类瞬时 UI 操作。参数为 null 时不需要 CanExecute 判断。</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

/// <summary>带参数同步命令：用于把步进/目标键等作为参数传入。</summary>
public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public bool CanExecute(object? parameter) => canExecute?.Invoke(Convert(parameter)) ?? true;
    public void Execute(object? parameter) => execute(Convert(parameter));
    private static T? Convert(object? parameter) => parameter is T typed ? typed : default;
}
