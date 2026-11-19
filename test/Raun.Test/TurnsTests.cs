using Xunit;

namespace Raun.Test;

public class TurnsTests
{
    [Fact]
    public async Task Work_does_not_start_while_earlier_work_is_still_running()
    {
        var turns = new Turns();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = false;

        var first = turns.TakeAsync(() => hold.Task);
        var second = turns.TakeAsync(() => { secondStarted = true; return Task.CompletedTask; });

        Assert.False(secondStarted);
        hold.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        await first;
        Assert.True(secondStarted);
    }

    [Fact]
    public async Task Work_that_throws_gives_up_its_turn()
    {
        var turns = new Turns();
        var ran = false;

        var failing = turns.TakeAsync(() => Task.FromException(new InvalidOperationException("boom")));
        await turns.TakeAsync(() => { ran = true; return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        Assert.True(ran);
    }
}
