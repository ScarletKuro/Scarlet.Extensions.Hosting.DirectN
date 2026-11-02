namespace Scarlet.Extensions.Hosting.DirectN.Internal;

internal sealed class DisposableList<T> : IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private readonly Lock _lock = new();
    private bool _disposeCalled;

    public void Add(T item)
    {
        if (item is IDisposable disposable)
        {
            lock (_lock)
            {
                _disposables.Add(disposable);
            }
        }
    }

    /// <summary>
    /// Disposes every item in reverse order, also when some of them throw.
    /// </summary>
    /// <exception cref="AggregateException">One or more items threw; all items were still disposed.</exception>
    public void Dispose()
    {
        List<Exception>? failures = null;
        lock (_lock)
        {
            if (_disposeCalled)
            {
                return;
            }

            _disposeCalled = true;
            for (var i = _disposables.Count - 1; i >= 0; i--)
            {
                try
                {
                    _disposables[i].Dispose();
                }
#pragma warning disable CA1031 // Collected and rethrown after the remaining items are disposed.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    (failures ??= []).Add(exception);
                }
            }

            _disposables.Clear();
        }

        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }
}
