using RetailFlow.Domain.Common;

namespace RetailFlow.UnitTests.Domain;

public class AggregateRootTests
{
    private sealed record SomethingHappened(Guid AggregateId) : DomainEvent;

    private sealed class TestAggregate(Guid id) : AggregateRoot<Guid>(id)
    {
        public void DoSomething() => Raise(new SomethingHappened(Id));
    }

    [Fact]
    public void Raise_RecordsTheEventUntilCleared()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());

        aggregate.DoSomething();

        var domainEvent = Assert.Single(aggregate.DomainEvents);
        Assert.IsType<SomethingHappened>(domainEvent);
    }

    [Fact]
    public void ClearDomainEvents_EmptiesTheList()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());
        aggregate.DoSomething();

        aggregate.ClearDomainEvents();

        Assert.Empty(aggregate.DomainEvents);
    }

    [Fact]
    public void Equality_IsByIdAlone_RegardlessOfInMemoryState()
    {
        var id = Guid.NewGuid();
        var loadedTwice = new TestAggregate(id);
        var loadedAgain = new TestAggregate(id);
        loadedAgain.DoSomething(); // differing in-memory state must not affect equality

        Assert.Equal(loadedTwice, loadedAgain);
    }

    [Fact]
    public void Equality_DiffersByIdEvenWithIdenticalState()
    {
        var first = new TestAggregate(Guid.NewGuid());
        var second = new TestAggregate(Guid.NewGuid());

        Assert.NotEqual(first, second);
    }
}
