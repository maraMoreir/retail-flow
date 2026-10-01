namespace RetailFlow.Domain.Fiscal;

/// <summary>The two Brazilian fiscal document types RetailFlow simulates (see README).</summary>
public enum FiscalDocumentType
{
    /// <summary>Nota Fiscal de Consumidor Eletrônica - retail point-of-sale receipt.</summary>
    Nfce,

    /// <summary>Nota Fiscal Eletrônica - general-purpose electronic invoice.</summary>
    Nfe,
}
