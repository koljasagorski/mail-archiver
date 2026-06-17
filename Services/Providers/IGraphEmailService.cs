using MailArchiver.Models;
using MailArchiver.Services.Providers.Graph;
using Microsoft.Graph.Models;

namespace MailArchiver.Services.Providers
{
    /// <summary>
    /// Service interface for Microsoft Graph email operations
    /// </summary>
    public interface IGraphEmailService
    {
        /// <summary>
        /// Syncs emails from Microsoft Graph API for M365 accounts
        /// </summary>
        /// <param name="account">The M365 mail account</param>
        /// <param name="jobId">Optional sync job ID for progress tracking</param>
        /// <returns>Task</returns>
        Task SyncMailAccountAsync(MailAccount account, string? jobId = null);

        /// <summary>
        /// Tests the connection to Microsoft Graph API
        /// </summary>
        /// <param name="account">The M365 mail account</param>
        /// <returns>True if connection is successful</returns>
        Task<bool> TestConnectionAsync(MailAccount account);

        /// <summary>
        /// Gets mail folders from Microsoft Graph API
        /// </summary>
        /// <param name="account">The M365 mail account</param>
        /// <returns>List of folder names</returns>
        Task<List<string>> GetMailFoldersAsync(MailAccount account);

        /// <summary>
        /// Restores an email to a specific folder using Microsoft Graph API
        /// </summary>
        /// <param name="email">The archived email to restore</param>
        /// <param name="targetAccount">The target M365 account</param>
        /// <param name="folderName">The target folder name</param>
        /// <returns>True if restoration is successful</returns>
        Task<bool> RestoreEmailToFolderAsync(ArchivedEmail email, MailAccount targetAccount, string folderName);

        /// <summary>
        /// Restores an email to a specific folder using Microsoft Graph API with optional folder structure preservation
        /// </summary>
        /// <param name="email">The archived email to restore</param>
        /// <param name="targetAccount">The target M365 account</param>
        /// <param name="folderName">The target folder name (base folder when preserving structure)</param>
        /// <param name="preserveFolderStructure">If true, recreates the original folder hierarchy under the target folder</param>
        /// <returns>True if restoration is successful</returns>
        Task<bool> RestoreEmailToFolderAsync(ArchivedEmail email, MailAccount targetAccount, string folderName, bool preserveFolderStructure);

        /// <summary>
        /// Deletes an email from the live Microsoft 365 mailbox (moves it to Deleted Items on the server).
        /// The local archive copy is not affected.
        /// </summary>
        /// <param name="email">The archived email whose original should be deleted on the server</param>
        /// <param name="account">The M365 account that the email was archived from</param>
        /// <returns>The outcome of the deletion attempt</returns>
        Task<MailboxDeletionResult> DeleteEmailFromMailboxAsync(ArchivedEmail email, MailAccount account);
    }
}