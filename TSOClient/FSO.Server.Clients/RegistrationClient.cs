using FSO.Common.Utils;
using FSO.Server.Clients.Framework;
using Newtonsoft.Json;
using RestSharp;
using System;
using System.Threading.Tasks;

namespace FSO.Server.Clients
{
    public class RegistrationResult
    {
        public bool Success;
        public string Error; // server error_description code (or "network_error"/"bad_response"); null on success
    }

    /// <summary>
    /// Server registration-mode discovery (userapi/registration/info). Contains only booleans - the
    /// registration key itself is never transmitted. Defaults are the PUBLIC (fail-open) mode, so a failed
    /// or absent discovery call leaves KeyRequired = false and the client behaves exactly as it did before.
    /// </summary>
    public class RegistrationInfo
    {
        public bool KeyRequired;
        public bool SmtpEnabled;
    }

    /// <summary>
    /// Talks to the server's userapi/registration endpoints for in-client account creation: request a 6-digit
    /// email code, then confirm it with a chosen username + password. All callbacks run on the game thread.
    /// </summary>
    public class RegistrationClient : AbstractHttpClient
    {
        public RegistrationClient(string baseUrl) : base(baseUrl) { }

        /// <summary>
        /// Ask the server whether registration needs an invite key (and whether it uses email verification).
        /// Fails OPEN: on any error the callback gets a default RegistrationInfo (KeyRequired = false), so the
        /// public flow is never blocked by an older server that lacks this endpoint or a transient network
        /// failure. The server still enforces the real key requirement when the form is actually submitted.
        /// </summary>
        public async Task GetInfo(Action<RegistrationInfo> callback)
        {
            var request = new RestRequest("userapi/registration/info", Method.Get);
            try
            {
                var response = await Client().ExecuteAsync(request);
                var info = ParseInfo(response?.Content);
                GameThread.NextUpdate(_ => callback(info));
            }
            catch
            {
                GameThread.NextUpdate(_ => callback(new RegistrationInfo()));
            }
        }

        public async Task RequestCode(string email, string confirmationUrl, Action<RegistrationResult> callback)
        {
            var request = new RestRequest("userapi/registration/request", Method.Post);
            request.AddParameter("email", email);
            request.AddParameter("confirmation_url", confirmationUrl);
            await Execute(request, callback);
        }

        public async Task ConfirmCode(string code, string email, string username, string password, string key, Action<RegistrationResult> callback)
        {
            var request = new RestRequest("userapi/registration/confirm", Method.Post);
            request.AddParameter("token", code);
            request.AddParameter("email", email); // must match the email the code was issued to (server binds them)
            request.AddParameter("username", username);
            request.AddParameter("password", password);
            // Invite-only servers require a registration key; sent only when we have one so the public flow
            // (empty key) is byte-for-byte identical to before. The server maps a wrong/absent key to key_wrong.
            if (!string.IsNullOrEmpty(key)) request.AddParameter("key", key);
            await Execute(request, callback);
        }

        private async Task Execute(RestRequest request, Action<RegistrationResult> callback)
        {
            try
            {
                var response = await Client().ExecuteAsync(request);
                var result = Parse(response?.Content);
                GameThread.NextUpdate(_ => callback(result));
            }
            catch
            {
                GameThread.NextUpdate(_ => callback(new RegistrationResult { Success = false, Error = "network_error" }));
            }
        }

        // Both endpoints return HTTP 200 with a JSON body: success = {status:"success"} (request) or a user
        // model (confirm); failure = {error, error_description}. On a failed confirmation-email send, request
        // reports {error:"registration_failed", error_description:"email_send_failed"}. The legacy
        // {status:"email_failed"} shape is still tolerated below for older/rolling server builds.
        private static RegistrationResult Parse(string content)
        {
            try
            {
                if (string.IsNullOrEmpty(content)) return Fail("bad_response");
                dynamic obj = JsonConvert.DeserializeObject(content);
                if (obj == null) return Fail("bad_response");
                if (obj.error != null)
                {
                    string err = (string)obj.error_description;
                    if (string.IsNullOrEmpty(err)) err = (string)obj.error;
                    return Fail(err);
                }
                if (obj.status != null && (string)obj.status == "email_failed") return Fail("email_failed");
                return new RegistrationResult { Success = true };
            }
            catch { return Fail("bad_response"); }
        }

        private static RegistrationResult Fail(string err) => new RegistrationResult { Success = false, Error = err };

        // Discovery is best-effort: any malformed/empty body fails open to the public-mode defaults
        // (KeyRequired = false), matching GetInfo's catch. Never throws.
        private static RegistrationInfo ParseInfo(string content)
        {
            try
            {
                if (string.IsNullOrEmpty(content)) return new RegistrationInfo();
                dynamic obj = JsonConvert.DeserializeObject(content);
                if (obj == null) return new RegistrationInfo();
                return new RegistrationInfo
                {
                    KeyRequired = obj.key_required != null && (bool)obj.key_required,
                    SmtpEnabled = obj.smtp_enabled != null && (bool)obj.smtp_enabled
                };
            }
            catch { return new RegistrationInfo(); }
        }
    }
}
