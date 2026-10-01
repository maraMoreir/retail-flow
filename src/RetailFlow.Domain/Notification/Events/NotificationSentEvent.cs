using RetailFlow.Domain.Common;

namespace RetailFlow.Domain.Notification.Events;

/// <summary>
/// Raised once a notification has actually been dispatched (not merely queued) -
/// the last step of the CreateSale saga's success path.
/// </summary>
public sealed record NotificationSentEvent(
    Guid NotificationId,
    Guid RecipientId,
    NotificationChannel Channel) : DomainEvent;
