using System.Data.Common;
using Dapper;
using FSO.Server.Database.DA;
using Microsoft.Data.Sqlite;
using Xunit;

// These tests mutate process-wide static state (Api.INSTANCE and the internal
// SqlEmailConfirmations.CodeSource seam), so they must not run concurrently. The suites here are tiny,
// so serial execution costs nothing and removes all cross-test interference.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FSO.Server.Api.Core.Tests
{
    /// <summary>
    /// Minimal <see cref="ISqlContext"/> over a caller-owned SQLite connection. The real SqliteContext is
    /// internal to FSO.Server.Database; this lets the token tests drive SqlEmailConfirmations against a
    /// throwaway in-memory database without touching production wiring.
    /// </summary>
    internal sealed class TestSqlContext : ISqlContext
    {
        private readonly SqliteConnection _conn;

        public TestSqlContext(SqliteConnection conn)
        {
            _conn = conn;
        }

        public bool SupportsFunctions => false;
        public bool UseBlobInventory => true;

        public DbConnection Connection
        {
            get
            {
                if (_conn.State != System.Data.ConnectionState.Open) _conn.Open();
                return _conn;
            }
        }

        public void Flush() { }

        // SqlEmailConfirmations issues raw SQL, so no MySQL->SQLite rewriting is needed here.
        public string CompatLayer(string sql, string updateKey = null) => sql;

        // The connection's lifetime is owned by the test, not the context.
        public void Dispose() { }
    }

    /// <summary>
    /// Creates the throwaway SQLite tables the DB-backed tests need. The <c>fso_email_confirm</c> schema
    /// mirrors migrations 0020/0032 plus the 0033 UNIQUE INDEX on <c>token</c> that the retry loop relies
    /// on; <c>fso_users</c> carries only what the registration path selects on.
    /// </summary>
    internal static class TestSchema
    {
        public static void CreateEmailConfirm(SqliteConnection conn)
        {
            conn.Execute(
                "CREATE TABLE IF NOT EXISTS fso_email_confirm (" +
                "  type INTEGER NOT NULL DEFAULT 1," +
                "  email TEXT," +
                "  token TEXT," +
                "  expires INTEGER," +
                "  tries INTEGER NOT NULL DEFAULT 0" +
                ");");
            // The source-of-truth uniqueness guarantee added by 0033_email_confirm_token_unique.sql.
            conn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS token_UNIQUE ON fso_email_confirm(token);");
        }

        public static void CreateUsers(SqliteConnection conn)
        {
            conn.Execute(
                "CREATE TABLE IF NOT EXISTS fso_users (" +
                "  user_id INTEGER PRIMARY KEY," +
                "  username TEXT," +
                "  email TEXT," +
                "  register_ip TEXT," +
                "  register_date INTEGER" +
                ");");
        }

        /// <summary>
        /// Mirrors migration 0034_email_send_log.sql: the durable per-email windowed attempt counter that
        /// RegistrationController / PasswordController use to rate limit verification / reset email sends.
        /// The (email, purpose) primary key is the source of truth SqlEmailSendLog relies on when folding a
        /// concurrent insert back into the increment path.
        /// </summary>
        public static void CreateEmailSendLog(SqliteConnection conn)
        {
            conn.Execute(
                "CREATE TABLE IF NOT EXISTS fso_email_send_log (" +
                "  email TEXT NOT NULL," +
                "  purpose INTEGER NOT NULL," +
                "  window_start INTEGER NOT NULL," +
                "  attempts INTEGER NOT NULL DEFAULT 0," +
                "  PRIMARY KEY (email, purpose)" +
                ");");
        }
    }
}
