namespace MailArchiver.Models
{
    public class ApiOptions
    {
        public const string Api = "Api";

        /// <summary>
        /// Enables the read-only REST API (e.g. for AI assistants like Claude).
        /// Disabled by default.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// API key clients must send via "Authorization: Bearer &lt;key&gt;" or "X-Api-Key" header.
        /// Must be at least 32 characters when the API is enabled.
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>
        /// Maximum number of emails returned per page.
        /// </summary>
        public int MaxPageSize { get; set; } = 200;

        /// <summary>
        /// Maximum number of characters of the plain-text body returned
        /// in list results when includeBody=true. Full bodies are always
        /// available via the single-email endpoint.
        /// </summary>
        public int BodyPreviewLength { get; set; } = 2000;
    }
}
