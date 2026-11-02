using Scarlet.Extensions.Hosting.DirectN.Internal;

namespace Scarlet.Extensions.Hosting.DirectN.Tests;

public class DisposableListTests
{
    [Fact]
    public void ThrowingItemsDontStopTheRestFromBeingDisposedInReverseOrder()
    {
        var disposed = new List<string>();
        var list = new DisposableList<object>();
        list.Add(new Item("first", disposed));
        list.Add(new Item("second", disposed, throws: true));
        list.Add(new object());
        list.Add(new Item("third", disposed, throws: true));

        var exception = Assert.Throws<AggregateException>(list.Dispose);

        Assert.Equal(["third", "second", "first"], disposed);
        Assert.Equal(["third", "second"], exception.InnerExceptions.Select(e => e.Message));
    }

    [Fact]
    public void DisposesOnlyOnce()
    {
        var disposed = new List<string>();
        var list = new DisposableList<object>();
        list.Add(new Item("only", disposed));

        list.Dispose();
        list.Dispose();

        Assert.Equal(["only"], disposed);
    }

    private sealed class Item(string name, List<string> disposed, bool throws = false) : IDisposable
    {
        public void Dispose()
        {
            disposed.Add(name);
            if (throws)
            {
                throw new InvalidOperationException(name);
            }
        }
    }
}
