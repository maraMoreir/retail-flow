namespace RetailFlow.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>e.g. amqp://guest:guest@localhost:5672</summary>
    public required string Uri { get; init; }
}
