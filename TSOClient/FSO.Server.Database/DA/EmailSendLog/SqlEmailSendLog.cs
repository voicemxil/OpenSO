using System;
using System.Linq;
using Dapper;
using FSO.Server.Common;

namespace FSO.Server.Database.DA.EmailSendLog
{
    public class SqlEmailSendLog : AbstractSqlDA, IEmailSendLog
    {
        public SqlEmailSendLog(ISqlContext context) : base(context)
        {
        }

        // Single atomic per-row statement: while the window is live, increment; once it has elapsed, roll the
        // row over to a fresh window at attempt 1. Applied as one UPDATE so two concurrent requests against the
        // same row can't lose a count (MySQL and SQLite both apply a row update atomically). window_start +
        // window is compared against now rather than now - window_start to avoid unsigned underflow if the
        // clock ever moves backwards.
        private const string RollOrIncrementSql =
            "UPDATE fso_email_send_log " +
            "SET attempts = CASE WHEN window_start + @window <= @now THEN 1 ELSE attempts + 1 END, " +
            "    window_start = CASE WHEN window_start + @window <= @now THEN @now ELSE window_start END " +
            "WHERE email = @email AND purpose = @purpose";

        public int RecordAttempt(string email, int purpose, uint window)
        {
            var key = Normalize(email);
            var now = Epoch.Now;

            var existing = Context.Connection.Query<EmailSendLogEntry>(
                "SELECT * FROM fso_email_send_log WHERE email = @email AND purpose = @purpose",
                new { email = key, purpose = purpose }).FirstOrDefault();

            if (existing == null)
            {
                // First attempt for this key: open a fresh window at attempt 1. The PRIMARY KEY on
                // (email, purpose) means a concurrent opener loses this INSERT with a duplicate-key error;
                // we fold that back into the increment path below so the attempt is still counted.
                try
                {
                    Context.Connection.Execute(
                        "INSERT INTO fso_email_send_log (email, purpose, window_start, attempts) VALUES (@email, @purpose, @now, 1)",
                        new { email = key, purpose = purpose, now = now });
                    return 1;
                }
                catch (Exception ex) when (IsDuplicateKeyError(ex))
                {
                    // Lost the insert race - a window row now exists; fall through and count against it.
                }
            }

            Context.Connection.Execute(RollOrIncrementSql,
                new { email = key, purpose = purpose, now = now, window = window });

            return Context.Connection.Query<int>(
                "SELECT attempts FROM fso_email_send_log WHERE email = @email AND purpose = @purpose",
                new { email = key, purpose = purpose }).FirstOrDefault();
        }

        /// <summary>
        /// Normalize the email key so casing and surrounding whitespace can't be used to bypass the counter.
        /// The DB collation may already be case-insensitive, but the trim (and not relying on collation) is
        /// what makes the key deterministic across engines.
        /// </summary>
        private static string Normalize(string email)
        {
            return (email ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>
        /// True if the exception represents a violation of the (email, purpose) PRIMARY KEY on
        /// fso_email_send_log, whichever ADO provider is backing this ISqlContext (MySQL in production,
        /// SQLite for embedded/test use). Mirrors SqlEmailConfirmations' duplicate-detection helper.
        /// </summary>
        private static bool IsDuplicateKeyError(Exception ex)
        {
            switch (ex)
            {
                case MySql.Data.MySqlClient.MySqlException mysqlEx:
                    // 1062 = ER_DUP_ENTRY
                    return mysqlEx.Number == 1062;
                case Microsoft.Data.Sqlite.SqliteException sqliteEx:
                    // 19 = SQLITE_CONSTRAINT
                    return sqliteEx.SqliteErrorCode == 19;
                default:
                    return false;
            }
        }
    }
}
