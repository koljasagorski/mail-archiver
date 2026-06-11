namespace MailArchiver.Services
{
    public record ArchiveQuestionResult(bool Success, string Answer, string? Error, int ToolCalls);

    public interface IArchiveQuestionService
    {
        /// <summary>
        /// Answers a free-form question about the email archive by letting Claude
        /// search the archive iteratively (full-text search + reading single emails).
        /// Email references in the answer are cited inline as [#id].
        /// </summary>
        Task<ArchiveQuestionResult> AskAsync(string question, CancellationToken cancellationToken = default);
    }
}
