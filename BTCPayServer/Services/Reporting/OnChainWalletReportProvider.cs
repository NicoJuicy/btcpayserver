#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Dapper;
using NBitcoin;

namespace BTCPayServer.Services.Reporting;

public class OnChainWalletReportProvider : ReportProvider
{
    public OnChainWalletReportProvider(
        NBXplorerConnectionFactory NbxplorerConnectionFactory,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        WalletRepository walletRepository)
    {
        this.NbxplorerConnectionFactory = NbxplorerConnectionFactory;
        StoreRepository = storeRepository;
        _handlers = handlers;
        WalletRepository = walletRepository;
    }

    private NBXplorerConnectionFactory NbxplorerConnectionFactory { get; }
    private StoreRepository StoreRepository { get; }
    private PaymentMethodHandlerDictionary _handlers;
    private WalletRepository WalletRepository { get; }
    public override string Name => "Wallets";
    ViewDefinition CreateViewDefinition()
    {
        return new()
        {
            Fields =
            {
                new ("Date", "datetime"),
                new ("Crypto", "string"),
                // For proper rendering of explorer links, Crypto should always be before tx_id
                new ("TransactionId", "tx_id"),
                new ("InvoiceId", "invoice_id"),
                new ("Confirmed", "boolean"),
                new ("BalanceChange", "amount")
            },
            Charts =
            {
                new ()
                {
                    Name = "Group by Crypto",
                    Totals = { "Crypto" },
                    Groups = { "Crypto", "Confirmed" },
                    Aggregates = { "BalanceChange" }
                }
            }
        };
    }

    public override bool IsAvailable()
    {
        return NbxplorerConnectionFactory.Available;
    }

    public override async Task Query(QueryContext queryContext, CancellationToken cancellation)
    {
        queryContext.ViewDefinition = CreateViewDefinition();
        await using var conn = await NbxplorerConnectionFactory.OpenConnection();
        var store = await StoreRepository.FindStore(queryContext.StoreId);
        if (store is null)
            return;
        var interval = DateTimeOffset.UtcNow - queryContext.From;
        foreach (var (pmi, settings) in store.GetPaymentMethodConfigs<DerivationSchemeSettings>(_handlers))
        {
            var network = ((IHasNetwork)_handlers[pmi]).Network;
            var walletId = new WalletId(store.Id, network.CryptoCode);
            var command = new CommandDefinition(
            commandText:
            "SELECT r.tx_id, r.seen_at, t.blk_id, t.blk_height, r.balance_change " +
            "FROM get_wallets_recent(@wallet_id, @code, @asset_id, @interval, NULL, NULL) r " +
            "JOIN txs t USING (code, tx_id) " +
            "ORDER BY r.seen_at",
            parameters: new
            {
                asset_id = GetAssetId(network),
                wallet_id = NBXplorer.Client.DBUtils.nbxv1_get_wallet_id(network.CryptoCode, settings.AccountDerivation.ToString()),
                code = network.CryptoCode,
                interval
            },
            cancellationToken: cancellation);

            var rows = await conn.QueryAsync(command);
            foreach (var r in rows)
            {
                var date = (DateTimeOffset)r.seen_at;
                if (date > queryContext.To)
                    continue;
                var values = queryContext.AddData();
                var balanceChange = Money.Satoshis((long)r.balance_change).ToDecimal(MoneyUnit.BTC);
                values.Add(date);
                values.Add(network.CryptoCode);
                values.Add((string)r.tx_id);
                values.Add(null);
                values.Add((long?)r.blk_height is not null);
                values.Add(new FormattedAmount(balanceChange, network.Divisibility).ToJObject());
            }
            var objects = await WalletRepository.GetWalletObjects(new GetWalletObjectsQuery
            {
                Ids = queryContext.Data.Select(d => (string)d[2]!).ToArray(),
                WalletId = walletId,
                Type = "tx"
            });
            foreach (var row in queryContext.Data)
            {
                if (!objects.TryGetValue(new WalletObjectId(walletId, "tx", (string)row[2]!), out var txObject))
                    continue;
                var invoiceId = txObject.GetLinks().Where(t => t.type == "invoice").Select(t => t.id).FirstOrDefault();
                row[3] = invoiceId;
            }
        }
    }

    private string? GetAssetId(BTCPayNetwork network)
    {
        if (network is Plugins.Altcoins.ElementsBTCPayNetwork elNetwork)
            return elNetwork.IsNativeAsset ? "" : elNetwork.AssetId.ToString();
        return null;
    }
}
