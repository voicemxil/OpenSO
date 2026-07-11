using System;
using System.IO;
using FSO.Server.Api.Core.Controllers;
using FSO.Server.Api.Core.Utils;
using FSO.Server.Database;
using FSO.Server.Database.DA;
using FSO.Server.Database.DA.EmailConfirmation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FSO.Server.Api.Core.Tests
{
    /// <summary>
    /// Area (b): registration delivery truthfulness. The registration request path must never report
    /// success when the confirmation email could not be delivered, and must not leave an orphaned pending
    /// token behind. These tests drive the real RegistrationController against a throwaway SQLite database;
    /// in the test environment there is no SMTP server (and no mail templates), so ApiMail.Send genuinely
    /// returns false, exercising the failure branch end to end.
    ///
    /// Documented gap: ApiMail/SmtpClient is concrete (constructs its own SmtpClient, reads Api.INSTANCE),
    /// so there is no seam to assert the SUCCESS branch without a live SMTP server. That path is not
    /// unit-tested here. The regression that actually matters - a failed send reported as success - is
    /// covered, because success is gated on the boolean ApiMail.Send returns.
    /// </summary>
    public class RegistrationDeliveryTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _connString;

        public RegistrationDeliveryTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), "openso_regtest_" + Guid.NewGuid().ToString("N") + ".db");
            _connString = "Data Source=" + _dbPath;
            using (var conn = new SqliteConnection(_connString))
            {
                conn.Open();
                TestSchema.CreateUsers(conn);
                TestSchema.CreateEmailConfirm(conn);
                // CreateToken now records a per-email send attempt before sending, so the counter table
                // must exist for these delivery tests to drive the controller.
                TestSchema.CreateEmailSendLog(conn);
            }
        }

        private Api NewApiWithSmtpEnabled()
        {
            var api = new Api(); // ctor assigns Api.INSTANCE = this
            api.Config = new ApiConfig
            {
                SmtpEnabled = true, // registration uses the email-confirmation flow
                UseProxy = false,
                Regkey = null
                // SmtpHost intentionally left null: delivery cannot succeed, so Send() returns false.
            };
            api.DAFactory = new SqliteDAFactory(new DatabaseConfiguration
            {
                Engine = "sqlite",
                ConnectionString = _connString
            });
            return api;
        }

        [Fact]
        public void CreateToken_WhenMailDeliveryFails_DoesNotReportSuccess()
        {
            NewApiWithSmtpEnabled();
            var controller = new RegistrationController();

            var result = controller.CreateToken(new ConfirmationCreateTokenModel
            {
                email = "newuser@example.test",
                confirmation_url = "https://openso.example/confirm?token=%token%"
            });

            var body = ContentOf(result);
            Assert.Contains("email_send_failed", body);
            Assert.Contains("registration_failed", body);
            Assert.DoesNotContain("\"status\":\"success\"", body);
        }

        [Fact]
        public void CreateToken_WhenMailDeliveryFails_RemovesOrphanedToken()
        {
            var api = NewApiWithSmtpEnabled();
            var controller = new RegistrationController();

            controller.CreateToken(new ConfirmationCreateTokenModel
            {
                email = "orphan@example.test",
                confirmation_url = "https://openso.example/confirm?token=%token%"
            });

            // A failed send must not strand a pending confirmation (it would cooldown-block an honest retry).
            using (var da = api.DAFactory.Get())
            {
                var pending = da.EmailConfirmations.GetByEmail("orphan@example.test", ConfirmationType.email);
                Assert.Null(pending);
            }
        }

        [Fact]
        public void ApiMail_Send_ReturnsFalse_WhenSmtpDisabled()
        {
            var api = new Api();
            api.Config = new ApiConfig { SmtpEnabled = false };

            var mail = new ApiMail("MailRegistrationToken");

            // Guards the old fire-and-forget bug: a send that cannot happen must report false, not true.
            Assert.False(mail.Send("x@example.test", "subject"));
        }

        private static string ContentOf(IActionResult result)
        {
            var content = Assert.IsType<ContentResult>(result);
            return content.Content;
        }

        public void Dispose()
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* temp file; best effort */ }
        }
    }
}
