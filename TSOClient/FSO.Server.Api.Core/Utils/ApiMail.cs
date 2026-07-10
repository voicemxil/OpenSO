using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Mail;

namespace FSO.Server.Api.Core.Utils
{
    /// <summary>
    /// Used to mail our users.
    /// Could be more useful in the future when out of Beta.
    /// </summary>
    public class ApiMail
    {
        string subject;
        string template;
        Dictionary<string, string> strings;

        public ApiMail(string template)
        {
            this.template = template;
            this.strings = new Dictionary<string, string>();
        }

        public void AddString(string key, string value)
        {
            strings.Add(key, value);
        }

        private string ComposeBody(Dictionary<string, string> strings)
        {
            string baseTemplate = File.ReadAllText("./MailTemplates/MailBase.html");
            string content = File.ReadAllText("./MailTemplates/" + template + ".html");

            foreach (KeyValuePair<string, string> entry in strings)
            {
                content = content.Replace("%" + entry.Key + "%", entry.Value);
            }

            strings = new Dictionary<string, string>();
            return baseTemplate.Replace("%content%", content);
        }

        /// <summary>
        /// Sends the composed mail and returns whether delivery to the SMTP server actually succeeded.
        /// This blocks until the SMTP transaction completes (bounded by <see cref="SmtpClient.Timeout"/>)
        /// so callers can report the real outcome to the user. Returns false — never throws — on any
        /// failure, logging the provider details server-side only. SMTP host/exception details are never
        /// returned to the caller.
        /// </summary>
        public bool Send(string to, string subject)
        {
            Api api = Api.INSTANCE;

            if (!api.Config.SmtpEnabled)
                return false;

            try
            {
                using (MailMessage message = new MailMessage())
                using (SmtpClient client = new SmtpClient())
                {
                    message.From = new MailAddress(api.Config.SmtpFrom ?? api.Config.SmtpUser, "OpenSO Staff");
                    message.To.Add(to);
                    message.Subject = subject;
                    message.IsBodyHtml = true;
                    message.Body = ComposeBody(strings);

                    // MUST be false: when true, SmtpClient ignores the NetworkCredential set below and tries
                    // the machine's default creds -> no SMTP auth, which providers like Brevo reject.
                    client.UseDefaultCredentials = false;

                    client.Host = api.Config.SmtpHost;
                    client.Port = api.Config.SmtpPort;
                    client.EnableSsl = true;
                    client.Credentials = new System.Net.NetworkCredential(api.Config.SmtpUser, api.Config.SmtpPassword);
                    // Cap how long a hung/slow SMTP server can stall the request (default is 100s).
                    client.Timeout = 15000;

                    // Block until the SMTP transaction completes. Previously this was a fire-and-forget
                    // SendMailAsync that returned true immediately, so a failed send still reported success
                    // to the registrant (an invisible dead end). Send() throws on any delivery failure.
                    client.Send(message);
                }

                return true;
            }
            catch (Exception ex)
            {
                // Log the real reason server-side only; never leak SMTP host/exception details to the caller.
                Console.WriteLine("[ApiMail] Failed to send \"" + subject + "\" to " + to + ": " + ex.Message);
                return false;
            }
        }
    }
}