using Raun;
using Xunit;

namespace Raun.Test;

/// <summary>
/// The runtime supplies <c>GetAwaiter</c> extensions so a scenario's tuple/array parallel forms
/// are honest, directly-runnable C#. These verify result aggregation, ordering, arity, and that
/// already-running elements are joined rather than serialized.
/// </summary>
public class AwaiterTests
{
    [Fact]
    public async Task Tuple_of_two_returns_both_results_in_order()
    {
        var (a, b) = await (Task.FromResult(1), Task.FromResult("x"));

        Assert.Equal(1, a);
        Assert.Equal("x", b);
    }

    [Fact]
    public async Task Tuple_of_three_returns_all_results()
    {
        var (a, b, c) = await (Task.FromResult(1), Task.FromResult(2), Task.FromResult(3));

        Assert.Equal((1, 2, 3), (a, b, c));
    }

    [Fact]
    public async Task Array_returns_all_results_in_order()
    {
        var result = await new[] { Task.FromResult(10), Task.FromResult(20), Task.FromResult(30) };

        Assert.Equal(new[] { 10, 20, 30 }, result);
    }

    [Fact]
    public async Task Tuple_joins_already_running_elements_without_serializing()
    {
        var gate = new TaskCompletionSource();
        var started = 0;

        async Task<int> Element(int value)
        {
            Interlocked.Increment(ref started);
            await gate.Task;
            return value;
        }

        // Both hot tasks reach their first await before the tuple is awaited.
        var first = Element(1);
        var second = Element(2);
        Assert.Equal(2, started);

        gate.SetResult();
        var (a, b) = await (first, second);

        Assert.Equal((1, 2), (a, b));
    }

    [Fact]
    public async Task Tuple_propagates_element_exception()
    {
        static Task<int> Boom() => Task.FromException<int>(new InvalidOperationException("boom"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await (Task.FromResult(1), Boom()));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Void_tuple_of_two_completes_both_elements()
    {
        var done = 0;

        async Task Element()
        {
            await Task.Yield();
            Interlocked.Increment(ref done);
        }

        await (Element(), Element());

        Assert.Equal(2, done);
    }

    [Fact]
    public async Task Void_tuple_of_eight_completes_all_elements()
    {
        var done = 0;

        async Task Element()
        {
            await Task.Yield();
            Interlocked.Increment(ref done);
        }

        await (Element(), Element(), Element(), Element(), Element(), Element(), Element(), Element());

        Assert.Equal(8, done);
    }

    [Fact]
    public async Task Void_tuple_joins_already_running_elements_without_serializing()
    {
        var gate = new TaskCompletionSource();
        var started = 0;

        async Task Element()
        {
            Interlocked.Increment(ref started);
            await gate.Task;
        }

        var first = Element();
        var second = Element();
        Assert.Equal(2, started);

        gate.SetResult();
        await (first, second);

        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Void_tuple_propagates_element_exception()
    {
        static Task Boom() => Task.FromException(new InvalidOperationException("boom"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await (Task.CompletedTask, Boom()));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Void_array_completes_all_elements()
    {
        var done = 0;

        async Task Element()
        {
            await Task.Yield();
            Interlocked.Increment(ref done);
        }

        await new[] { Element(), Element(), Element() };

        Assert.Equal(3, done);
    }

    [Fact]
    public async Task Generic_array_still_yields_typed_results_beside_the_void_overload()
    {
        // Task<int>[] converts to Task[] by array covariance, so both array overloads apply;
        // the identity match must win so the results are not lost.
        int[] result = await new[] { Task.FromResult(1), Task.FromResult(2) };

        Assert.Equal(new[] { 1, 2 }, result);
    }

    [Fact]
    public async Task Void_tuple_arities_three_to_seven_complete_every_element()
    {
        var done = 0;

        async Task E()
        {
            await Task.Yield();
            Interlocked.Increment(ref done);
        }

        await (E(), E(), E());
        await (E(), E(), E(), E());
        await (E(), E(), E(), E(), E());
        await (E(), E(), E(), E(), E(), E());
        await (E(), E(), E(), E(), E(), E(), E());

        Assert.Equal(3 + 4 + 5 + 6 + 7, done);
    }

    [Fact]
    public async Task Generic_tuple_still_yields_typed_results_beside_the_void_overload()
    {
        // (Task<int>, Task<string>) converts to (Task, Task) by tuple conversion, so both tuple
        // overloads apply; the identity match must win so the results are not lost.
        (int a, string b) = await (Task.FromResult(1), Task.FromResult("x"));

        Assert.Equal((1, "x"), (a, b));
    }

    [Fact]
    public async Task Mixed_tuple_awaits_as_void_and_discards_typed_results()
    {
        // A group mixing Task and Task<T> binds the all-Task overload: it joins, and the typed
        // results are discarded, the same as awaiting a single typed step without binding it.
        var typed = Task.FromResult(42);

        await (Task.CompletedTask, typed);

        Assert.True(typed.IsCompletedSuccessfully);
    }
}
