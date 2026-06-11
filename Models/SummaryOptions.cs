namespace MailArchiver.Models
{
    public class SummaryOptions
    {
        public const string Summary = "Summary";

        /// <summary>
        /// Enables AI-generated daily email summaries (Summaries page in the web UI).
        /// Disabled by default.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Anthropic API key used to call Claude (create one at console.anthropic.com).
        /// Required when summaries are enabled.
        /// </summary>
        public string? AnthropicApiKey { get; set; }

        /// <summary>
        /// Claude model used for summarization.
        /// </summary>
        public string Model { get; set; } = "claude-sonnet-4-6";

        /// <summary>
        /// Time of day (server local time, HH:mm) at which the daily summary is generated.
        /// </summary>
        public string DailyExecutionTime { get; set; } = "07:00";

        /// <summary>
        /// Language the summaries are written in (e.g. "en", "de", "fr").
        /// </summary>
        public string Language { get; set; } = "en";

        /// <summary>
        /// Optional additional instructions appended to the summarization prompt,
        /// e.g. to exclude certain topics ("Ignore all client-related emails,
        /// only summarize internal topics like HR and IT").
        /// </summary>
        public string? CustomInstructions { get; set; }

        /// <summary>
        /// Maximum number of emails (newest first) included in one summary.
        /// </summary>
        public int MaxEmails { get; set; } = 100;

        /// <summary>
        /// Maximum number of plain-text body characters per email passed to the model.
        /// </summary>
        public int MaxBodyCharsPerEmail { get; set; } = 1500;

        /// <summary>
        /// Period covered by each summary, counted backwards from the time of generation.
        /// </summary>
        public int PeriodHours { get; set; } = 24;

        /// <summary>
        /// Maximum tokens the model may use for the summary response.
        /// </summary>
        public int MaxOutputTokens { get; set; } = 4000;

        /// <summary>
        /// Maximum number of tool-use rounds (searches / email reads) Claude may
        /// perform when answering a free-form question on the Summaries page.
        /// </summary>
        public int MaxQuestionIterations { get; set; } = 10;
    }
}
