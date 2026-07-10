using System;
using System.Collections.Generic;
using System.Threading;
using Dapper;
using FSO.Server.Database.DA.EmailConfirmation;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FSO.Server.Api.Core.Tests
{
    /// <summary>
    /// Area (c): duplicate-token prevention. SqlEmailConfirmations.Create inserts optimistically and relies
    /// on the UNIQUE INDEX on fso_email_confirm.token (migration 0033) to reject a colliding code, then
    /// draws a fresh code and retries. These tests run against a real in-memory SQLite database carrying
    /// that index and drive the code source deterministically (via the internal CodeSource seam) so the
    /// collision/retry path is exercised for real rather than by luck.
    /// </summary>
    public class EmailConfirmationTokenTests : IDisposable
    {
        private const uint FarFuture = 2000000000u;
        private readonly SqliteConnection _conn;

        public EmailConfirmationTokenTests()
        {
            _conn = new SqliteConnection("Data Source=:memory:");
            _conn.Open();
            TestSchema.CreateEmailConfirm(_conn);
        }

        private SqlEmailConfirmations NewDao() => new SqlEmailConfirmations(new TestSqlContext(_conn));

        [Fact]
        public void UniqueIndex_RejectsTwoRowsWithTheSameToken()
        {
            InsertRaw("123456");

            var ex = Assert.Throws<SqliteException>(() => InsertRaw("123456"));
            Assert.Equal(19, ex.SqliteErrorCode); // SQLITE_CONSTRAINT - the guarantee Create depends on
            Assert.Equal(1L, Count("123456"));
        }

        [Fact]
        public void Create_RetriesAndRegenerates_OnTokenCollision()
        {
            InsertRaw("555555"); // a code already issued to someone else
            var codes = new Queue<string>(new[] { "555555", "777777" }); // first collides, second is free

            WithCodeSource(() => codes.Dequeue(), () =>
            {
                var token = NewDao().Create(new EmailConfirmation
                {
                    type = ConfirmationType.email,
                    email = "a@b.test",
                    expires = FarFuture
                });

                Assert.Equal("777777", token); // retried past the collision to a free code
                Assert.Empty(codes);            // consumed exactly two candidates
            });

            Assert.Equal(1L, Count("555555")); // the pre-existing row is untouched (no duplicate landed)
            Assert.Equal(1L, Count("777777")); // the new row landed exactly once
        }

        [Fact]
        public void Create_NeverLandsADuplicate_AndFailsLoudlyWhenExhausted()
        {
            InsertRaw("999999");

            WithCodeSource(() => "999999", () => // every candidate collides with the existing row
            {
                Assert.Throws<InvalidOperationException>(() => NewDao().Create(new EmailConfirmation
                {
                    type = ConfirmationType.email,
                    email = "c@d.test",
                    expires = FarFuture
                }));
            });

            Assert.Equal(1L, Count("999999")); // a second usable row with the same token was never created
        }

        [Fact]
        public void Create_AllocatesDistinctTokens_AcrossManyCalls()
        {
            var next = 100000;
            WithCodeSource(() => Interlocked.Increment(ref next).ToString(), () =>
            {
                var dao = NewDao();
                var seen = new HashSet<string>();
                for (var i = 0; i < 50; i++)
                {
                    var token = dao.Create(new EmailConfirmation
                    {
                        type = ConfirmationType.email,
                        email = "u" + i + "@e.test",
                        expires = FarFuture
                    });
                    Assert.True(seen.Add(token), "duplicate token allocated: " + token);
                }
            });

            Assert.Equal(50L, CountAll());
        }

        // ---- helpers ----

        private void InsertRaw(string token) =>
            _conn.Execute(
                "INSERT INTO fso_email_confirm (type, email, token, expires) VALUES (1, 'x@x.test', @t, @e)",
                new { t = token, e = FarFuture });

        private long Count(string token) =>
            _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM fso_email_confirm WHERE token = @t", new { t = token });

        private long CountAll() =>
            _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM fso_email_confirm");

        private static void WithCodeSource(Func<string> source, Action body)
        {
            var original = SqlEmailConfirmations.CodeSource;
            SqlEmailConfirmations.CodeSource = source;
            try { body(); }
            finally { SqlEmailConfirmations.CodeSource = original; }
        }

        public void Dispose() => _conn.Dispose();
    }
}
