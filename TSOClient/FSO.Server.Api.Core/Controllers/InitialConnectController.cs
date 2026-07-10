using FSO.Server.Api.Core.Utils;
using FSO.Server.Common;
using FSO.Server.Protocol.CitySelector;
using FSO.Server.Servers.Api.JsonWebToken;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Net;

namespace FSO.Server.Api.Core.Controllers
{
    [Route("cityselector/app/InitialConnectServlet")]
    [ApiController]
    public class InitialConnectController : ControllerBase
    {
        private static Func<IActionResult> ERROR_MISSING_TOKEN = ApiResponse.XmlFuture(HttpStatusCode.OK, new XMLErrorMessage("501", "Token not found"));
        private static Func<IActionResult> ERROR_EXPIRED_TOKEN = ApiResponse.XmlFuture(HttpStatusCode.OK, new XMLErrorMessage("502", "Token has expired"));

        // A client is treated as Windows when it advertises a win-* rid, OR when it sends no rid at all
        // (legacy client) - defaulting to Windows preserves the existing in-game patch flow. Non-Windows
        // rids (linux-x64, osx-x64, osx-arm64, ...) are never handed the win-x64 in-game payload.
        // internal (not private) purely so the verification tests can assert this decision directly; the
        // behaviour is identical either way (see FSO.Server.Api.Core.Tests).
        internal static bool IsWindowsClient(string rid)
            => string.IsNullOrEmpty(rid) || rid.StartsWith("win", StringComparison.OrdinalIgnoreCase);

        [HttpGet]
        public IActionResult Get(string ticket, string version, string rid)
        {
            var api = Api.INSTANCE;

            if (ticket == null || ticket == "" || version == null){
                return ERROR_MISSING_TOKEN();
            }

            using (var db = api.DAFactory.Get())
            {
                var dbTicket = db.AuthTickets.Get(ticket);
                if (dbTicket == null){
                    return ERROR_MISSING_TOKEN();
                }

                db.AuthTickets.Delete((string)ticket);
                if (dbTicket.date + api.Config.AuthTicketDuration < Epoch.Now){
                    return ERROR_EXPIRED_TOKEN();
                }

                /** Is it a valid account? **/
                var user = db.Users.GetById(dbTicket.user_id);
                if (user == null){
                    return ERROR_MISSING_TOKEN();
                }

                //Use JWT to create and sign an auth cookies
                var session = new JWTUser()
                {
                    UserID = user.user_id,
                    UserName = user.username
                };

                //TODO: This assumes 1 shard, when using multiple need to either have version download occour after
                //avatar select, or rework the tables
                var shardOne = api.Shards.GetById(1);

                var token = api.JWT.CreateToken(session);

                // The compatibility response advertises the target version (branch + number) to every client;
                // that alone drives the client's version-mismatch check. The update PAYLOAD url, however, is
                // platform-specific: both the managed full_zip and the configured UpdateUrl are the WINDOWS
                // in-game patch (a win-x64 zip). Only Windows clients get it - non-Windows clients receive the
                // compatibility info with NO package url and update through the OpenSO Launcher, which selects
                // their platform's build from the per-RID distribution manifest. This keeps the win-x64 zip
                // from ever being advertised to a Linux/macOS client as "the" update.
                string updateUrl = null;
                if (IsWindowsClient(rid))
                {
                    updateUrl = (shardOne.UpdateID != null)
                        ? db.Updates.GetUpdate(shardOne.UpdateID.Value)?.full_zip
                        : api.Config.UpdateUrl;
                }

                IActionResult response = ApiResponse.Xml(HttpStatusCode.OK, new UserAuthorized()
                {
                    FSOBranch = shardOne.VersionName,
                    FSOVersion = shardOne.VersionNumber,
                    FSOUpdateUrl = updateUrl,
                    FSOCDNUrl = api.Config.CDNUrl
                });
                Response.Cookies.Append("fso", token.Token, new Microsoft.AspNetCore.Http.CookieOptions()
                {
                    Expires = DateTimeOffset.Now.AddDays(1),
                    Path = "/"
                });
                //HttpContext.Current.Response.SetCookie(new HttpCookie("fso", token.Token));
                return response;
            }
        }

    }
}
 