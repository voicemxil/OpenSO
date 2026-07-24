using Dapper;
using System;

namespace FSO.Server.Database.DA.Transactions
{
    public class SqlTransactions : AbstractSqlDA, ITransactions
    {
        public SqlTransactions(ISqlContext context) : base(context)
        {
        }

        public void Purge(int day)
        {
            Context.Connection.Query("DELETE FROM fso_transactions WHERE day < @day", new { day = day });
        }

        /// <summary>
        /// Record a money movement that was applied OUTSIDE SqlAvatars.Transaction - currently the Edit A
        /// Sim makeover charge, which debits inside its own budget-guarded UPDATE so a short balance can
        /// never half-apply the edit. Without this the simoleons would leave the economy invisibly.
        ///
        /// Same daily-aggregate upsert Transaction uses: one row per (from, to, type, day), with value
        /// summed and count incremented. Pass uint.MaxValue for a counterparty that isn't an avatar or
        /// object (money leaving the economy entirely).
        ///
        /// transaction_type must stay clear of 41-50 - JobBalanceTask aggregates exactly that range to
        /// rebalance money-object payouts, so a non-object type in there would skew the daily rates.
        /// </summary>
        public void Log(uint from_id, uint to_id, int transaction_type, int value)
        {
            var day = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalDays;
            Context.Connection.Execute(Context.CompatLayer(
                "INSERT INTO fso_transactions (from_id, to_id, transaction_type, day, value, count) " +
                "VALUES (@from_id, @to_id, @transaction_type, @day, @value, @count) " +
                "ON DUPLICATE KEY UPDATE value = value + @value, count = count+1",
                "`from_id`,`to_id`,`transaction_type`,`day`"), new
                {
                    from_id = from_id,
                    to_id = to_id,
                    transaction_type = transaction_type,
                    day = day,
                    value = value,
                    count = 1
                });
        }
    }
}
