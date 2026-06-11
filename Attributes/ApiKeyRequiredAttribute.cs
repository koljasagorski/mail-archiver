using MailArchiver.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace MailArchiver.Attributes
{
    /// <summary>
    /// Protects API endpoints with a static API key configured in the "Api" section.
    /// The key is accepted either as "Authorization: Bearer &lt;key&gt;" or as "X-Api-Key" header.
    /// </summary>
    public class ApiKeyRequiredAttribute : Attribute, IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var options = context.HttpContext.RequestServices
                .GetRequiredService<IOptions<ApiOptions>>().Value;
            var logger = context.HttpContext.RequestServices
                .GetRequiredService<ILogger<ApiKeyRequiredAttribute>>();

            if (!options.Enabled || string.IsNullOrWhiteSpace(options.ApiKey))
            {
                context.Result = new ObjectResult(new { error = "The REST API is disabled. Set Api:Enabled to true and configure Api:ApiKey to use it." })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
                return;
            }

            var providedKey = ExtractApiKey(context.HttpContext.Request);
            if (providedKey == null || !FixedTimeEquals(providedKey, options.ApiKey))
            {
                var clientIp = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                logger.LogWarning("Rejected API request to {Path} from {ClientIp}: missing or invalid API key",
                    context.HttpContext.Request.Path, clientIp);

                context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
                context.Result = new ObjectResult(new { error = "Missing or invalid API key. Provide it via 'Authorization: Bearer <key>' or 'X-Api-Key' header." })
                {
                    StatusCode = StatusCodes.Status401Unauthorized
                };
            }
        }

        private static string? ExtractApiKey(HttpRequest request)
        {
            var authHeader = request.Headers.Authorization.FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) &&
                authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return authHeader.Substring("Bearer ".Length).Trim();
            }

            var apiKeyHeader = request.Headers["X-Api-Key"].FirstOrDefault();
            return string.IsNullOrWhiteSpace(apiKeyHeader) ? null : apiKeyHeader.Trim();
        }

        private static bool FixedTimeEquals(string provided, string expected)
        {
            var providedBytes = Encoding.UTF8.GetBytes(provided);
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
        }
    }
}
