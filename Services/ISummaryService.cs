using MailArchiver.Models;

namespace MailArchiver.Services
{
    public interface ISummaryService
    {
        /// <summary>
        /// Generates and stores an AI summary of the archived emails of the configured
        /// period (default: last 24 hours). Returns the stored summary; when generation
        /// fails, a summary with IsSuccess = false and an error message is stored instead.
        /// Returns null when another generation is already in progress.
        /// </summary>
        Task<DailySummary?> GenerateAsync(string triggeredBy, CancellationToken cancellationToken = default);
    }
}
