using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using JQLite;

namespace JQLite.Tests;

/// <summary>
/// System.Reactive over the streaming <c>Evaluate</c>: which adapters to <see cref="IObservable{T}"/>
/// let a subscriber (<c>Take</c>, a disposal) stop the evaluation, as returning <see langword="false"/>
/// from the callback does.
/// </summary>
/// <remarks>
/// The interpreter pushes every result from within one call. A disposal stops it only if it reaches
/// the callback before that call returns: an adapter that evaluates inside <c>Subscribe</c> gives its
/// <see cref="IDisposable"/> too late, and Rx can only drop the results that follow.
/// </remarks>
public class JqObservableTests
{
    const int Count = 100_000;

    static readonly JqExpression Range = Jq.Parse($"range({Count})");

    static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    // Evaluates in Subscribe: the IDisposable exists only once the evaluation has ended.
    static IObservable<JsonElement> Eager(JqExpression expression, JsonElement input, Counter produced)
        => Observable.Create<JsonElement>(observer =>
        {
            Run(expression, input, observer, produced, () => false);
            return Disposable.Empty;
        });

    // Evaluates in Subscribe and watches the token of the subscription: still too late, since the
    // token is cancelled through the IDisposable that Subscribe has not returned yet.
    static IObservable<JsonElement> WithToken(JqExpression expression, JsonElement input, Counter produced)
        => Observable.Create<JsonElement>((observer, cancellation) =>
        {
            Run(expression, input, observer, produced, () => cancellation.IsCancellationRequested);
            return Task.CompletedTask;
        });

    // Returns its IDisposable first and evaluates on the scheduler, by default the current thread's
    // trampoline, which runs it once the operators of the query have their subscriptions: disposing
    // it stops the evaluation at the next result.
    static IObservable<JsonElement> Deferred(JqExpression expression, JsonElement input, Counter produced, IScheduler? scheduler = null)
        => Observable.Create<JsonElement>(observer =>
        {
            var subscription = new BooleanDisposable();
            var run = (scheduler ?? Scheduler.CurrentThread).Schedule(() => Run(expression, input, observer, produced, () => subscription.IsDisposed));
            return new CompositeDisposable(run, subscription);
        });

    static void Run(JqExpression expression, JsonElement input, IObserver<JsonElement> observer, Counter produced, Func<bool> stopped)
    {
        try
        {
            expression.Evaluate(input, result =>
            {
                produced.Value++;
                observer.OnNext(result);
                return !stopped();
            });
        }
        catch (JqException error)
        {
            observer.OnError(error);
            return;
        }
        finally
        {
            produced.Ended.Set();
        }

        observer.OnCompleted();
    }

    [Fact]
    public void Eager_adapter_evaluates_everything_after_Take()
    {
        var produced = new Counter();
        var seen = Eager(Range, Json("null"), produced).Take(3).Select(static e => e.GetInt32()).ToEnumerable().ToArray();
        Assert.Equal([0, 1, 2], seen);
        Assert.Equal(Count, produced.Value);
    }

    [Fact]
    public void Token_adapter_evaluates_everything_after_Take()
    {
        var produced = new Counter();
        var seen = WithToken(Range, Json("null"), produced).Take(3).Select(static e => e.GetInt32()).ToEnumerable().ToArray();
        Assert.Equal([0, 1, 2], seen);
        Assert.Equal(Count, produced.Value);
    }

    [Fact]
    public void Deferred_adapter_stops_at_Take()
    {
        var produced = new Counter();
        var seen = new List<int>();
        using var subscription = Deferred(Range, Json("null"), produced).Take(3).Subscribe(e => seen.Add(e.GetInt32()));
        Assert.Equal([0, 1, 2], seen);
        Assert.Equal(3, produced.Value);
    }

    [Fact]
    public void Deferred_adapter_stops_at_TakeWhile()
    {
        var produced = new Counter();
        var seen = new List<int>();
        using var subscription = Deferred(Range, Json("null"), produced).Select(static e => e.GetInt32()).TakeWhile(static i => i < 5).Subscribe(seen.Add);
        Assert.Equal([0, 1, 2, 3, 4], seen);
        // TakeWhile needs the first result that fails it.
        Assert.Equal(6, produced.Value);
    }

    [Fact]
    public async Task Deferred_adapter_stops_on_its_scheduler()
    {
        var produced = new Counter();
        var seen = await Deferred(Range, Json("null"), produced, TaskPoolScheduler.Default).Take(3).Select(static e => e.GetInt32()).ToList();
        Assert.Equal([0, 1, 2], seen);
        Assert.True(produced.Ended.Wait(TimeSpan.FromSeconds(30)));
        // Results may come before Take has the subscription to dispose: a few more, never all.
        Assert.InRange(produced.Value, 3, Count - 1);
    }

    [Fact]
    public async Task Deferred_adapter_evaluates_everything_when_subscribed_on_another_thread()
    {
        // SubscribeOn subscribes on a thread with no trampoline: the adapter's own subscription
        // starts one and runs it out before returning its IDisposable.
        var produced = new Counter();
        var seen = await Deferred(Range, Json("null"), produced).SubscribeOn(TaskPoolScheduler.Default).Take(3).Select(static e => e.GetInt32()).ToList();
        Assert.Equal([0, 1, 2], seen);
        Assert.True(produced.Ended.Wait(TimeSpan.FromSeconds(30)));
        Assert.Equal(Count, produced.Value);
    }

    [Fact]
    public async Task Deferred_adapter_stops_when_awaiting_the_first()
    {
        var produced = new Counter();
        var first = await Deferred(Range, Json("null"), produced).FirstAsync();
        Assert.Equal(0, first.GetInt32());
        Assert.Equal(1, produced.Value);
    }

    [Fact]
    public void Deferred_adapter_completes_after_every_result()
    {
        var produced = new Counter();
        var seen = Deferred(Jq.Parse(".[]"), Json("[1,2,3]"), produced).Materialize().Select(static n => n.HasValue ? n.Value.GetRawText() : n.Kind.ToString()).ToEnumerable().ToArray();
        Assert.Equal(["1", "2", "3", "OnCompleted"], seen);
    }

    [Fact]
    public void Deferred_adapter_delivers_results_before_an_error()
    {
        var produced = new Counter();
        var seen = Deferred(Jq.Parse("1, 2, error(\"late\"), 3"), Json("null"), produced).Materialize()
            .Select(static n => n.HasValue ? n.Value.GetRawText() : n.Exception?.Message ?? n.Kind.ToString()).ToEnumerable().ToArray();
        Assert.Equal(["1", "2", "late"], seen);
    }

    sealed class Counter
    {
        public int Value;

        public readonly ManualResetEventSlim Ended = new();
    }
}
