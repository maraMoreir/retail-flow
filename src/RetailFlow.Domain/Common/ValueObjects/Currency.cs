namespace RetailFlow.Domain.Common.ValueObjects;

/// <summary>
/// Deliberately a closed set, not a free-text ISO 4217 string - RetailFlow's fiscal
/// documents (NFC-e/NF-e) are Brazilian, so BRL is the only currency any real sale
/// will use; USD/EUR exist for completeness (e.g. supplier invoices) rather than
/// multi-currency storefronts.
/// </summary>
public enum Currency
{
    BRL,
    USD,
    EUR,
}
