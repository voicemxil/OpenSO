using System;
using System.Linq;
using Dapper;
using FSO.Server.Common;

namespace FSO.Server.Database.DA.EmailConfirmation
{
    public class SqlEmailConfirmations : AbstractSqlDA, IEmailConfirmations
    {
        public SqlEmailConfirmations(ISqlContext context) : base(context)
        {

        }

        public EmailConfirmation GetByToken(string token)
        {
            var confirm = Context.Connection.Query<EmailConfirmation>("SELECT * FROM fso_email_confirm WHERE token = @token", new { token = token }).FirstOrDefault();
            
            if(confirm==null) { return null; }

            if(Epoch.Now > confirm.expires)
            {
                Remove(confirm.token);
                return null;
            }

            return confirm;
        }

        public EmailConfirmation GetByEmail(string email, ConfirmationType type)
        {
            var confirm = Context.Connection.Query<EmailConfirmation>("SELECT * FROM fso_email_confirm WHERE email = @email AND type = @type", new { email = email, type = type }).FirstOrDefault();

            if (confirm == null) { return null; }

            if (Epoch.Now > confirm.expires)
            {
                Remove(confirm.token);
                return null;
            }

            return confirm;
        }

        // Bounded so a persistently unlucky (or genuinely broken) run fails loudly instead of looping forever.
        private const int MAX_TOKEN_ATTEMPTS = 25;

        // Source of each candidate code. Defaults to the real cryptographic 6-digit generator, so production
        // behaviour is unchanged; the verification tests (FSO.Server.Api.Core.Tests) override it to feed a
        // deterministic sequence and exercise the duplicate-collision retry loop below. Never assigned in
        // production.
        internal static Func<string> CodeSource = SixDigitCode;

        public string Create(EmailConfirmation confirm)
        {
            // A short 6-digit code rather than a GUID, so it can be typed into the in-client registration
            // dialog (it also rides in the website confirmation link's URL). The `token_UNIQUE` index added
            // by migration 0033_email_confirm_token_unique.sql is the source of truth for uniqueness: we
            // INSERT optimistically and let the DB reject a colliding code, rather than the old
            // SELECT COUNT(*)-then-INSERT check. That check-then-insert had a race window between the read
            // and the write where two concurrent requests could both "pass" for the same code and each
            // insert a row, allocating a genuine duplicate token. On a collision here we just draw a fresh
            // code and retry. Brute-forcing the small space is blocked by the per-IP confirm rate-limiter in
            // RegistrationController.
            for (int attempt = 0; attempt < MAX_TOKEN_ATTEMPTS; attempt++)
            {
                confirm.token = CodeSource();
                try
                {
                    Context.Connection.Execute(
                        "INSERT INTO fso_email_confirm (type, email, token, expires) VALUES (@type, @email, @token, @expires)",
                        confirm);
                    return confirm.token;
                }
                catch (Exception ex) when (IsDuplicateTokenError(ex))
                {
                    // Another request claimed this exact code first (or, vanishingly unlikely, a genuine
                    // collision) - draw a new one and try again.
                }
            }

            throw new InvalidOperationException(
                $"Could not allocate a unique email confirmation token after {MAX_TOKEN_ATTEMPTS} attempts.");
        }

        /// <summary>
        /// True if the exception represents a violation of the `token_UNIQUE` constraint on
        /// fso_email_confirm, whichever ADO provider is backing this ISqlContext (MySQL in production,
        /// SQLite for embedded/local use).
        /// </summary>
        private static bool IsDuplicateTokenError(Exception ex)
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

        private static string SixDigitCode()
        {
            var bytes = new byte[4];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return (BitConverter.ToUInt32(bytes, 0) % 1000000u).ToString("000000");
        }

        /// <summary>
        /// Records a failed confirm attempt against a pending confirmation and returns the new
        /// attempt count (0 if no such token exists).
        /// </summary>
        public int IncrementTries(string token)
        {
            Context.Connection.Execute("UPDATE fso_email_confirm SET tries = tries + 1 WHERE token = @token", new { token = token });
            return Context.Connection.Query<int>("SELECT tries FROM fso_email_confirm WHERE token = @token", new { token = token }).FirstOrDefault();
        }

        public void Remove(string token)
        {
            Context.Connection.Execute("DELETE FROM fso_email_confirm WHERE token = @token", new { token = token });
        }
    }
}
