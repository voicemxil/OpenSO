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

        public string Create(EmailConfirmation confirm)
        {
            // A short 6-digit code rather than a GUID, so it can be typed into the in-client registration
            // dialog (it also rides in the website confirmation link's URL). Kept unique among current pending
            // confirmations so GetByToken stays unambiguous. Brute-forcing the small space is blocked by the
            // per-IP confirm rate-limiter in RegistrationController.
            string code; int tries = 0;
            do { code = SixDigitCode(); }
            while (++tries < 25 && Context.Connection.Query<long>("SELECT COUNT(*) FROM fso_email_confirm WHERE token = @t", new { t = code }).First() > 0);
            confirm.token = code;
            Context.Connection.Execute("INSERT INTO fso_email_confirm (type, email, token, expires) VALUES (@type, @email, @token, @expires)", confirm);
            return confirm.token;
        }

        private static string SixDigitCode()
        {
            var bytes = new byte[4];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return (BitConverter.ToUInt32(bytes, 0) % 1000000u).ToString("000000");
        }

        public void Remove(string token)
        {
            Context.Connection.Execute("DELETE FROM fso_email_confirm WHERE token = @token", new { token = token });
        }
    }
}
