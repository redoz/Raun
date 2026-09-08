using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Raun;

/// <summary>
/// <c>GetAwaiter</c> extensions that make a scenario's explicit parallel forms directly
/// awaitable C#: a tuple of tasks awaits to a tuple of results, and an array of tasks awaits to
/// an array of results. The elements are already-running (hot) tasks, so awaiting joins them via
/// <see cref="Task.WhenAll(System.Threading.Tasks.Task[])"/> rather than serializing.
///
/// Two families are covered: every element a <c>Task&lt;T&gt;</c> (the await yields the results) and
/// every element a plain <c>Task</c> (the await yields nothing — the usual shape for a group of
/// assertion steps). A group that mixes the two binds the plain-<c>Task</c> family, because
/// <c>Task&lt;T&gt;</c> converts to <c>Task</c> by reference and tuple/array receivers admit that: it
/// joins, and the typed results are discarded. The all-<c>Task&lt;T&gt;</c> family still wins for
/// all-typed groups because identity is the better conversion. <c>ValueTask</c> elements have no
/// overload here and cannot be grouped.
///
/// The generator still lowers these forms into individual step nodes; these awaiters exist so the
/// authored scenario method remains honest, compilable, runnable C#.
/// </summary>
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method",
    Justification = "Each result is read only after Task.WhenAll has completed the tasks, so .Result does not block.")]
public static class ScenarioAwaiters
{
    public static TaskAwaiter<T[]> GetAwaiter<T>(this Task<T>[] tasks)
        => Task.WhenAll(tasks).GetAwaiter();

    public static TaskAwaiter GetAwaiter(this Task[] tasks)
        => Task.WhenAll(tasks).GetAwaiter();

    public static TaskAwaiter GetAwaiter(this (Task first, Task second) tasks)
        => Task.WhenAll(tasks.first, tasks.second).GetAwaiter();

    public static TaskAwaiter GetAwaiter(this (Task, Task, Task) tasks)
        => Task.WhenAll(tasks.Item1, tasks.Item2, tasks.Item3).GetAwaiter();

    public static TaskAwaiter GetAwaiter(this (Task, Task, Task, Task) tasks)
        => Task.WhenAll(tasks.Item1, tasks.Item2, tasks.Item3, tasks.Item4).GetAwaiter();

    public static TaskAwaiter GetAwaiter(this (Task, Task, Task, Task, Task) tasks)
        => Task.WhenAll(tasks.Item1, tasks.Item2, tasks.Item3, tasks.Item4, tasks.Item5).GetAwaiter();

    public static TaskAwaiter GetAwaiter(this (Task, Task, Task, Task, Task, Task) tasks)
        => Task.WhenAll(tasks.Item1, tasks.Item2, tasks.Item3, tasks.Item4, tasks.Item5, tasks.Item6)
            .GetAwaiter();

    public static TaskAwaiter GetAwaiter(this (Task, Task, Task, Task, Task, Task, Task) tasks)
        => Task.WhenAll(tasks.Item1, tasks.Item2, tasks.Item3, tasks.Item4, tasks.Item5, tasks.Item6,
            tasks.Item7).GetAwaiter();

    public static TaskAwaiter GetAwaiter(this (Task, Task, Task, Task, Task, Task, Task, Task) tasks)
        => Task.WhenAll(tasks.Item1, tasks.Item2, tasks.Item3, tasks.Item4, tasks.Item5, tasks.Item6,
            tasks.Item7, tasks.Item8).GetAwaiter();

    public static TaskAwaiter<(T1, T2)> GetAwaiter<T1, T2>(
        this (Task<T1> first, Task<T2> second) tasks)
        => Combine(tasks.first, tasks.second).GetAwaiter();

    public static TaskAwaiter<(T1, T2, T3)> GetAwaiter<T1, T2, T3>(
        this (Task<T1>, Task<T2>, Task<T3>) tasks)
        => Combine(tasks.Item1, tasks.Item2, tasks.Item3).GetAwaiter();

    public static TaskAwaiter<(T1, T2, T3, T4)> GetAwaiter<T1, T2, T3, T4>(
        this (Task<T1>, Task<T2>, Task<T3>, Task<T4>) tasks)
        => Combine(tasks.Item1, tasks.Item2, tasks.Item3, tasks.Item4).GetAwaiter();

    public static TaskAwaiter<(T1, T2, T3, T4, T5)> GetAwaiter<T1, T2, T3, T4, T5>(
        this (Task<T1>, Task<T2>, Task<T3>, Task<T4>, Task<T5>) tasks)
        => Combine(tasks.Item1, tasks.Item2, tasks.Item3, tasks.Item4, tasks.Item5).GetAwaiter();

    public static TaskAwaiter<(T1, T2, T3, T4, T5, T6)> GetAwaiter<T1, T2, T3, T4, T5, T6>(
        this (Task<T1>, Task<T2>, Task<T3>, Task<T4>, Task<T5>, Task<T6>) tasks)
        => Combine(tasks.Item1, tasks.Item2, tasks.Item3, tasks.Item4, tasks.Item5, tasks.Item6)
            .GetAwaiter();

    public static TaskAwaiter<(T1, T2, T3, T4, T5, T6, T7)> GetAwaiter<T1, T2, T3, T4, T5, T6, T7>(
        this (Task<T1>, Task<T2>, Task<T3>, Task<T4>, Task<T5>, Task<T6>, Task<T7>) tasks)
        => Combine(tasks.Item1, tasks.Item2, tasks.Item3, tasks.Item4, tasks.Item5, tasks.Item6,
            tasks.Item7).GetAwaiter();

    public static TaskAwaiter<(T1, T2, T3, T4, T5, T6, T7, T8)>
        GetAwaiter<T1, T2, T3, T4, T5, T6, T7, T8>(
            this (Task<T1>, Task<T2>, Task<T3>, Task<T4>, Task<T5>, Task<T6>, Task<T7>, Task<T8>)
                tasks)
        => Combine(tasks.Item1, tasks.Item2, tasks.Item3, tasks.Item4, tasks.Item5, tasks.Item6,
            tasks.Item7, tasks.Item8).GetAwaiter();

    private static async Task<(T1, T2)> Combine<T1, T2>(Task<T1> t1, Task<T2> t2)
    {
        await Task.WhenAll(t1, t2).ConfigureAwait(false);
        return (t1.Result, t2.Result);
    }

    private static async Task<(T1, T2, T3)> Combine<T1, T2, T3>(Task<T1> t1, Task<T2> t2, Task<T3> t3)
    {
        await Task.WhenAll(t1, t2, t3).ConfigureAwait(false);
        return (t1.Result, t2.Result, t3.Result);
    }

    private static async Task<(T1, T2, T3, T4)> Combine<T1, T2, T3, T4>(
        Task<T1> t1, Task<T2> t2, Task<T3> t3, Task<T4> t4)
    {
        await Task.WhenAll(t1, t2, t3, t4).ConfigureAwait(false);
        return (t1.Result, t2.Result, t3.Result, t4.Result);
    }

    private static async Task<(T1, T2, T3, T4, T5)> Combine<T1, T2, T3, T4, T5>(
        Task<T1> t1, Task<T2> t2, Task<T3> t3, Task<T4> t4, Task<T5> t5)
    {
        await Task.WhenAll(t1, t2, t3, t4, t5).ConfigureAwait(false);
        return (t1.Result, t2.Result, t3.Result, t4.Result, t5.Result);
    }

    private static async Task<(T1, T2, T3, T4, T5, T6)> Combine<T1, T2, T3, T4, T5, T6>(
        Task<T1> t1, Task<T2> t2, Task<T3> t3, Task<T4> t4, Task<T5> t5, Task<T6> t6)
    {
        await Task.WhenAll(t1, t2, t3, t4, t5, t6).ConfigureAwait(false);
        return (t1.Result, t2.Result, t3.Result, t4.Result, t5.Result, t6.Result);
    }

    private static async Task<(T1, T2, T3, T4, T5, T6, T7)> Combine<T1, T2, T3, T4, T5, T6, T7>(
        Task<T1> t1, Task<T2> t2, Task<T3> t3, Task<T4> t4, Task<T5> t5, Task<T6> t6, Task<T7> t7)
    {
        await Task.WhenAll(t1, t2, t3, t4, t5, t6, t7).ConfigureAwait(false);
        return (t1.Result, t2.Result, t3.Result, t4.Result, t5.Result, t6.Result, t7.Result);
    }

    private static async Task<(T1, T2, T3, T4, T5, T6, T7, T8)> Combine<T1, T2, T3, T4, T5, T6, T7, T8>(
        Task<T1> t1, Task<T2> t2, Task<T3> t3, Task<T4> t4, Task<T5> t5, Task<T6> t6, Task<T7> t7,
        Task<T8> t8)
    {
        await Task.WhenAll(t1, t2, t3, t4, t5, t6, t7, t8).ConfigureAwait(false);
        return (t1.Result, t2.Result, t3.Result, t4.Result, t5.Result, t6.Result, t7.Result,
            t8.Result);
    }
}
