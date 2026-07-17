using Dapper;
using FSO.Server.Database.DA.Tuning;
using FSO.Server.Database.SqliteCompat;

namespace FSO.Server.Database.DA
{
    public class MySqlDAFactory : IDAFactory
    {
        private DatabaseConfiguration Config;

        public MySqlDAFactory(DatabaseConfiguration config)
        {
            this.Config = config;

            // MySQL returns ENUM columns as strings; Dapper needs this handler to materialize
            // DbUppercaseEnum properties (e.g. fso_tuning.owner_type). SqliteDAFactory registers
            // its own set including this one.
            SqlMapper.AddTypeHandler(new DbEnumHandler<DbTuningType>());
        }

        public IDA Get()
        {
            return new SqlDA(new MySqlContext(Config.ConnectionString));
        }
    }
}
