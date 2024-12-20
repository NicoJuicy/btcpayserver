using System.Collections.Generic;
using BTCPayServer.Data;

namespace BTCPayServer.Components.StoreRecentInvoices;

public class StoreRecentInvoicesViewModel
{
    public string StoreId { get; set; }
    public IList<StoreRecentInvoiceViewModel> Invoices { get; set; } = new List<StoreRecentInvoiceViewModel>();
    public bool InitialRendering { get; set; }
    public string CryptoCode { get; set; }
}
