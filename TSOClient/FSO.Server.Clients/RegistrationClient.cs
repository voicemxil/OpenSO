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
    /// Talks to the server's userapi/registration endpoints for in-client account creation: request a 6-digit
    /// email code, then confirm it with a chosen username + password. All callbacks run on the game thread.
    /// </summary>
    public class RegistrationClient : AbstractHttpClient
    {
        public RegistrationClient(string baseUrl) : base(baseUrl) { }

        public async Task RequestCode(string email, string confirmationUrl, Action<RegistrationResult> callback)
        {
            var request = new RestRequest("userapi/registration/request", Method.Post);
            request.AddParameter("email", email);
            request.AddParameter("confirmation_url", confirmationUrl);
            await Execute(request, callback);
        }

        public async Task ConfirmCode(string code, string username, string password, Action<RegistrationResult> callback)
        {
            var request = new RestRequest("userapi/registration/confirm", Method.Post);
            request.AddParameter("token", code);
            request.AddParameter("username", username);
            request.AddParameter("password", password);
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
        // model (confirm); failure = {error, error_description}. request may also report {status:"email_failed"}.
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
    }
}
