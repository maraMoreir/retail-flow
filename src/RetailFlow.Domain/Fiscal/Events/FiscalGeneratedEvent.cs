using RetailFlow.Domain.Common;

namespace RetailFlow.Domain.Fiscal.Events;

/// <summary>
/// Raised once a fiscal document is authorized for a completed sale. The saga's
/// success path from here moves on to customer notification; this is also the
/// last step before the saga marks itself complete.
/// </summary>
public sealed record FiscalGeneratedEvent(
    Guid SaleId,
    Guid FiscalDocumentId,
    FiscalDocumentType DocumentType,
    string AuthorizationCode) : DomainEvent;
