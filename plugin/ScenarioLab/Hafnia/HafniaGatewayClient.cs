using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ScenarioLab.Hafnia
{
    /// <summary>Today's Hafnia allowance, from GET /api/System/Information.</summary>
    internal sealed class Quota
    {
        public int Used;
        public int Limit;
    }

    /// <summary>One Hafnia request ("summary") as the gateway reports it.</summary>
    internal sealed class Summary
    {
        public string Id;
        public string Progress;   // NotStarted, FetchingVideo, TransferringAssets, RequestingSummary, Completed, Failed
        public string Response;   // the answer, when Completed
        public string LastError;  // the reason, when Failed

        public bool IsFinished => Progress == "Completed" || Progress == "Failed";
    }

    internal sealed class GatewayException : Exception
    {
        public GatewayException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Talks to the Hafnia gateway's REST API on this machine (https://localhost:15443), following the hackathon kit's
    /// gateway-api.md: log in once, send the session as the X-Session-Id header, POST /api/Summaries to ask, GET
    /// /api/Summaries/{id} to read the answer.
    ///
    /// Cost: only CreateSummaryAsync costs a Hafnia request (1 of 500 a day, when it completes). Every other call is free.
    /// The password is never stored: it is passed to LoginAsync and only the session ID is kept, in memory.
    /// JSON uses .NET Framework's built-in JavaScriptSerializer (System.Web.Extensions), so no extra NuGet package.
    /// </summary>
    internal sealed class HafniaGatewayClient
    {
        public const string Address = "https://localhost:15443";

        private readonly HttpClient _http;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private volatile string _sessionId;

        public HafniaGatewayClient()
        {
            var handler = new HttpClientHandler
            {
                // The gateway's certificate is self-signed (kit). Accept certificate errors only for this machine
                // (localhost); any other address still gets the normal checks.
                ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                    errors == SslPolicyErrors.None || request.RequestUri.IsLoopback
            };
            _http = new HttpClient(handler) { BaseAddress = new Uri(Address), Timeout = TimeSpan.FromSeconds(60) };
        }

        public bool IsLoggedIn => _sessionId != null;

        /// <summary>POST /api/Sessions/Login. A session lasts 8 hours; max 10 logins a minute on the machine.</summary>
        public async Task LoginAsync(string user, string password)
        {
            var body = new Dictionary<string, object> { { "Name", user }, { "Password", password } };
            var answer = await SendAsync(HttpMethod.Post, "/api/Sessions/Login", body, needsSession: false);
            _sessionId = GetString(answer, "Id");
            if (_sessionId == null)
            {
                throw new GatewayException("The gateway accepted the login but returned no session.");
            }
        }

        /// <summary>GET /api/System/Information (free, no session needed). Null if the allowance is not available.</summary>
        public async Task<Quota> GetQuotaAsync()
        {
            var info = await SendAsync(HttpMethod.Get, "/api/System/Information", null, needsSession: false);
            object value;
            var quota = info.TryGetValue("QuotaInformation", out value) ? value as Dictionary<string, object> : null;
            if (quota == null)
            {
                return null;
            }
            return new Quota
            {
                Used = Convert.ToInt32(quota["DailySummaryCount"], CultureInfo.InvariantCulture),
                Limit = Convert.ToInt32(quota["DailySummaryQuota"], CultureInfo.InvariantCulture)
            };
        }

        /// <summary>Finds a saved prompt's ID by its exact title (IDs change when prompts are imported). Null if none.</summary>
        public async Task<string> FindPromptIdAsync(string title)
        {
            var answer = await SendAsync(HttpMethod.Get, "/api/Prompts?Limit=100&SearchString=" + Uri.EscapeDataString(title), null, needsSession: true);
            object page;
            if (!answer.TryGetValue("PageContent", out page) || !(page is IEnumerable))
            {
                return null;
            }
            var match = ((IEnumerable)page).OfType<Dictionary<string, object>>()
                .FirstOrDefault(p => GetString(p, "Title") == title);
            return match == null ? null : GetString(match, "Id");
        }

        /// <summary>
        /// POST /api/Summaries: ask Hafnia VLM about one clip. COSTS 1 REQUEST once it completes.
        /// promptId makes the gateway rules for that saved prompt check the answer; the text sent in prompt is what
        /// Hafnia receives (verified on p043, see hafnia/README.md).
        /// </summary>
        public async Task<Summary> CreateSummaryAsync(string promptId, string prompt, string cameraId, DateTime startUtc, DateTime endUtc)
        {
            // Kit: UTC with a "Z", never an offset such as +02:00 (in the address "+" reads as a space).
            const string format = "yyyy-MM-dd'T'HH:mm:ss'Z'";
            string uri = "xprotect://localhost/cameras/" + cameraId
                + "?StartTime=" + startUtc.ToString(format, CultureInfo.InvariantCulture)
                + "&EndTime=" + endUtc.ToString(format, CultureInfo.InvariantCulture);

            var body = new Dictionary<string, object>
            {
                { "PromptId", promptId },
                { "Prompt", prompt },
                { "VideoUris", new[] { uri } }
            };
            return ToSummary(await SendAsync(HttpMethod.Post, "/api/Summaries", body, needsSession: true));
        }

        /// <summary>GET /api/Summaries/{id} (free).</summary>
        public async Task<Summary> GetSummaryAsync(string id)
        {
            return ToSummary(await SendAsync(HttpMethod.Get, "/api/Summaries/" + Uri.EscapeDataString(id), null, needsSession: true));
        }

        private static Summary ToSummary(Dictionary<string, object> answer)
        {
            return new Summary
            {
                Id = GetString(answer, "Id"),
                Progress = GetString(answer, "Progress"),
                Response = GetString(answer, "Response"),
                LastError = GetString(answer, "LastError")
            };
        }

        private async Task<Dictionary<string, object>> SendAsync(HttpMethod method, string path, object body, bool needsSession)
        {
            using (var request = new HttpRequestMessage(method, path))
            {
                if (body != null)
                {
                    request.Content = new StringContent(_json.Serialize(body), Encoding.UTF8, "application/json");
                }
                if (needsSession)
                {
                    string session = _sessionId;
                    if (session == null)
                    {
                        throw new GatewayException("Not connected to the Hafnia gateway: type the password and press Connect.");
                    }
                    request.Headers.Add("X-Session-Id", session);
                }

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request);
                }
                catch (HttpRequestException ex)
                {
                    // Show the innermost reason (for example a TLS or connection error).
                    Exception inner = ex;
                    while (inner.InnerException != null)
                    {
                        inner = inner.InnerException;
                    }
                    throw new GatewayException("No answer from the gateway at " + Address + ": " + inner.Message);
                }

                using (response)
                {
                    string text = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized && needsSession)
                        {
                            _sessionId = null;   // expired, or the gateway restarted: log in again
                        }
                        throw new GatewayException(Describe(response.StatusCode, path, text));
                    }
                    return string.IsNullOrWhiteSpace(text)
                        ? new Dictionary<string, object>()
                        : _json.Deserialize<Dictionary<string, object>>(text);
                }
            }
        }

        /// <summary>Error texts from the kit's "When a call fails" table.</summary>
        private static string Describe(HttpStatusCode status, string path, string body)
        {
            string detail = string.IsNullOrWhiteSpace(body) ? "" : " " + (body.Length > 300 ? body.Substring(0, 300) + "…" : body);
            switch ((int)status)
            {
                case 400: return "The gateway rejected the request (400):" + detail;
                case 401: return path.EndsWith("/Login", StringComparison.OrdinalIgnoreCase)
                    ? "Wrong gateway password (401)."
                    : "Gateway session expired (401): press Connect again.";
                case 404: return "Not found on the gateway (404): " + path;
                case 429: return "Too many logins in the last minute (429): wait a minute.";
                case 500: return "The gateway failed (500): see C:\\ProgramData\\VideoOS\\Hafnia.Vlm.Gateway\\logs." + detail;
                default: return "The gateway answered " + (int)status + "." + detail;
            }
        }

        private static string GetString(Dictionary<string, object> values, string key)
        {
            object value;
            return values != null && values.TryGetValue(key, out value) && value != null ? value.ToString() : null;
        }
    }
}
