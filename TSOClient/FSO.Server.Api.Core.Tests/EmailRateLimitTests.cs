using System;
using System.IO;
using Dapper;
using FSO.Server.Api.Core.Controllers;
using FSO.Server.Database;
using FSO.Server.Database.DA;
using FSO.Server.Database.DA.EmailConfirmation;
using FSO.Server.Database.DA.EmailSendLog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FSO.Server.Api.Core.Tests
{
    /// <summary>
    /// Per-email rate limiting of verification / password-reset email sends. Two layers are exercised:
    ///
    ///   1. <see cref="SqlEmailSendLog"/> against a real in-memory SQLite database carrying the
    ///      fso_email_send_log schema (migration 0034): counting, window rollover, email-key normalization
    ///      and per-purpose separation.
    ///   2. The real <see cref="RegistrationController"/> / <see cref="PasswordController"/> driven end to
    ///      end against throwaway SQLite: the cap refuses a send once exceeded, attempts are counted even
    ///      when the send FAILS and the token is deleted (closing the tight-retry hole), the window
    ///      expiring restores service, and email casing can't bypass the key.
    ///
    /// In the test environment there is no SMTP server, so a real send returns <c>email_send_failed</c>;
    /// a refused request instead returns <c>email_rate_limited</c>, which the controller returns BEFORE the
    /// send call - so seeing <c>email_rate_limited</c> (and never <c>email_send_failed</c>) proves no send
    /// was attempted.
    /// </summary>
    public class EmailRateLimitTests : IDisposable
    {
        private const uint Window = 3600;

        // ---- DA layer: SqlEmailSendLog over in-memory SQLite ----

        private readonly SqliteConnection _conn;

        public EmailRateLimitTests()
        {
            _conn = new SqliteConnection("Data Source=:memory:");
            _conn.Open();
            TestSchema.CreateEmailSendLog(_conn);
        }

        private SqlEmailSendLog NewDao() => new SqlEmailSendLog(new TestSqlContext(_conn));

        [Fact]
        public void RecordAttempt_IncrementsWithinWindow()
        {
            var dao = NewDao();
            Assert.Equal(1, dao.RecordAttempt("a@b.test", (int)ConfirmationType.email, Window));
            Assert.Equal(2, dao.RecordAttempt("a@b.test", (int)ConfirmationType.email, Window));
            Assert.Equal(3, dao.RecordAttempt("a@b.test", (int)ConfirmationType.email, Window));
            Assert.Equal(1L, RowCount()); // one counter row, not one row per attempt
        }

        [Fact]
        public void RecordAttempt_RollsOverWhenWindowElapsed()
        {
            var dao = NewDao();
            dao.RecordAttempt("a@b.test", (int)ConfirmationType.email, Window);
            dao.RecordAttempt("a@b.test", (int)ConfirmationType.email, Window); // count now 2

            // Push the window start into the past so the stored window has elapsed.
            _conn.Execute("UPDATE fso_email_send_log SET window_start = window_start - @back WHERE email = 'a@b.test'",
                new { back = Window + 100 });

            // The elapsed window rolls over to a fresh count of 1 rather than continuing to climb.
            Assert.Equal(1, dao.RecordAttempt("a@b.test", (int)ConfirmationType.email, Window));
        }

        [Fact]
        public void RecordAttempt_NormalizesEmailCaseAndWhitespace()
        {
            var dao = NewDao();
            Assert.Equal(1, dao.RecordAttempt("User@Example.Test ", (int)ConfirmationType.email, Window));
            // Different casing + surrounding whitespace must hit the same counter, not a fresh one.
            Assert.Equal(2, dao.RecordAttempt("  user@example.test", (int)ConfirmationType.email, Window));
            Assert.Equal(1L, RowCount());
            Assert.Equal(1L, _conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM fso_email_send_log WHERE email = 'user@example.test'"));
        }

        [Fact]
        public void RecordAttempt_SeparatesByPurpose()
        {
            var dao = NewDao();
            dao.RecordAttempt("a@b.test", (int)ConfirmationType.email, Window);
            dao.RecordAttempt("a@b.test", (int)ConfirmationType.email, Window); // email count 2
            // Password reset for the same address is an independent bucket.
            Assert.Equal(1, dao.RecordAttempt("a@b.test", (int)ConfirmationType.password, Window));
            Assert.Equal(2L, RowCount());
        }

        private long RowCount() => _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM fso_email_send_log");

        // ---- Controller layer: real controllers over throwaway file-backed SQLite ----

        private string NewFileDb()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "openso_ratelimit_" + Guid.NewGuid().ToString("N") + ".db");
            _tempDbs.Add(dbPath);
            using (var conn = new SqliteConnection("Data Source=" + dbPath))
            {
                conn.Open();
                TestSchema.CreateUsers(conn);
                TestSchema.CreateEmailConfirm(conn);
                TestSchema.CreateEmailSendLog(conn);
            }
            return dbPath;
        }

        private static Api NewApi(string dbPath)
        {
            var api = new Api(); // ctor assigns Api.INSTANCE = this
            api.Config = new ApiConfig
            {
                SmtpEnabled = true, // email-confirmation flow; SmtpHost left null so a real send returns false
                UseProxy = false,
                Regkey = null
            };
            api.DAFactory = new SqliteDAFactory(new DatabaseConfiguration
            {
                Engine = "sqlite",
                ConnectionString = "Data Source=" + dbPath
            });
            return api;
        }

        private static IActionResult RequestVerification(RegistrationController controller, string email)
        {
            return controller.CreateToken(new ConfirmationCreateTokenModel
            {
                email = email,
                confirmation_url = "https://openso.example/confirm?token=%token%"
            });
        }

        [Fact]
        public void Registration_RefusesAfterCap_CountingFailedSends()
        {
            var dbPath = NewFileDb();
            var api = NewApi(dbPath);
            var controller = new RegistrationController();
            const string email = "victim@example.test";

            // Every send fails (no SMTP) and deletes its token, so surviving-token counting would never
            // trip. The cap must count ATTEMPTS: exactly EMAIL_SEND_MAX_PER_WINDOW sends are allowed through.
            for (var i = 0; i < RegistrationController.EMAIL_SEND_MAX_PER_WINDOW; i++)
            {
                var body = ContentOf(RequestVerification(controller, email));
                Assert.Contains("email_send_failed", body);
                Assert.DoesNotContain("email_rate_limited", body);
            }

            // The next attempt is refused WITHOUT a send, even though no token from the prior attempts survives.
            var refused = ContentOf(RequestVerification(controller, email));
            Assert.Contains("email_rate_limited", refused);
            Assert.Contains("registration_failed", refused);
            Assert.DoesNotContain("email_send_failed", refused); // proves the send path was not entered

            // The refused request created no pending confirmation.
            using (var da = api.DAFactory.Get())
            {
                Assert.Null(da.EmailConfirmations.GetByEmail(email, ConfirmationType.email));
            }
        }

        [Fact]
        public void Registration_RateLimit_NotBypassableByEmailCasing()
        {
            var dbPath = NewFileDb();
            NewApi(dbPath);
            var controller = new RegistrationController();

            for (var i = 0; i < RegistrationController.EMAIL_SEND_MAX_PER_WINDOW; i++)
            {
                Assert.Contains("email_send_failed", ContentOf(RequestVerification(controller, "user@example.test")));
            }

            // A mixed-case / padded spelling of the same address must not reset the counter.
            var refused = ContentOf(RequestVerification(controller, "  USER@Example.Test "));
            Assert.Contains("email_rate_limited", refused);
            Assert.DoesNotContain("email_send_failed", refused);
        }

        [Fact]
        public void Registration_WindowExpiry_RestoresService()
        {
            var dbPath = NewFileDb();
            NewApi(dbPath);
            var controller = new RegistrationController();
            const string email = "expiry@example.test"; // already lowercase == its normalized key

            for (var i = 0; i < RegistrationController.EMAIL_SEND_MAX_PER_WINDOW; i++)
            {
                RequestVerification(controller, email);
            }
            Assert.Contains("email_rate_limited", ContentOf(RequestVerification(controller, email)));

            // Age the window out; the next request should be served again (reaching the send, which fails).
            using (var conn = new SqliteConnection("Data Source=" + dbPath))
            {
                conn.Open();
                conn.Execute("UPDATE fso_email_send_log SET window_start = window_start - @back WHERE email = @email",
                    new { back = (long)RegistrationController.EMAIL_SEND_WINDOW_SECS + 100, email = email });
            }

            var afterExpiry = ContentOf(RequestVerification(controller, email));
            Assert.Contains("email_send_failed", afterExpiry);
            Assert.DoesNotContain("email_rate_limited", afterExpiry);
        }

        [Fact]
        public void Password_RefusesAfterCap_CountingFailedSends()
        {
            var dbPath = NewFileDb();
            var api = NewApi(dbPath);
            const string email = "member@example.test";

            // Password reset requires an existing account for this email (else email_invalid, no send).
            using (var conn = new SqliteConnection("Data Source=" + dbPath))
            {
                conn.Open();
                conn.Execute("INSERT INTO fso_users (user_id, username, email, register_ip, register_date) " +
                    "VALUES (1, 'member', @email, '127.0.0.1', 0)", new { email = email });
            }

            var controller = new PasswordController();

            for (var i = 0; i < PasswordController.EMAIL_SEND_MAX_PER_WINDOW; i++)
            {
                var body = ContentOf(controller.CreatePwdToken(new ConfirmationCreateTokenModel
                {
                    email = email,
                    confirmation_url = "https://openso.example/reset?token=%token%"
                }));
                Assert.Contains("email_send_failed", body);
            }

            var refused = ContentOf(controller.CreatePwdToken(new ConfirmationCreateTokenModel
            {
                email = email,
                confirmation_url = "https://openso.example/reset?token=%token%"
            }));
            Assert.Contains("email_rate_limited", refused);
            Assert.Contains("password_reset_failed", refused);
            Assert.DoesNotContain("email_send_failed", refused);
        }

        private static string ContentOf(IActionResult result)
        {
            var content = Assert.IsType<ContentResult>(result);
            return content.Content;
        }

        private readonly System.Collections.Generic.List<string> _tempDbs = new System.Collections.Generic.List<string>();

        public void Dispose()
        {
            _conn.Dispose();
            foreach (var db in _tempDbs)
            {
                try { if (File.Exists(db)) File.Delete(db); } catch { /* temp file; best effort */ }
            }
        }
    }
}
