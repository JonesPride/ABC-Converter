using ABC.Converter.App.Commands;
using Xunit;

namespace ABC.Converter.App.Tests.Commands;

public sealed class AsyncCommandTests
{
    [Fact]
    public async Task DisablesExecutionWhileRunningAndRejectsReentrancy()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int executionCount = 0;
        var canExecuteStates = new List<bool>();
        var command = new AsyncCommand(async () =>
        {
            executionCount++;
            started.TrySetResult();
            await release.Task;
        });
        command.CanExecuteChanged += (_, _) =>
            canExecuteStates.Add(command.CanExecute(null));

        Task firstExecution = command.ExecuteAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(command.CanExecute(null));
        Task rejectedExecution = command.ExecuteAsync();
        release.TrySetResult();
        await Task.WhenAll(firstExecution, rejectedExecution);

        Assert.Equal(1, executionCount);
        Assert.True(command.CanExecute(null));
        Assert.Contains(false, canExecuteStates);
        Assert.True(canExecuteStates[^1]);
    }

    [Fact]
    public async Task HonorsTheExternalCanExecutePredicate()
    {
        bool allowed = false;
        int executionCount = 0;
        var command = new AsyncCommand(
            () =>
            {
                executionCount++;
                return Task.CompletedTask;
            },
            () => allowed);

        Assert.False(command.CanExecute(null));
        await command.ExecuteAsync();
        Assert.Equal(0, executionCount);

        allowed = true;

        Assert.True(command.CanExecute(null));
        await command.ExecuteAsync();
        Assert.Equal(1, executionCount);
    }
}
