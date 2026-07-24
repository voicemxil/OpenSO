namespace FSO.Server.Database.DA.Transactions
{
    public interface ITransactions
    {
        void Purge(int day);
        void Log(uint from_id, uint to_id, int transaction_type, int value);
    }
}
