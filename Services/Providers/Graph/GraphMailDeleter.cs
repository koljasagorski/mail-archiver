using MailArchiver.Models;
using Microsoft.Graph;

namespace MailArchiver.Services.Providers.Graph
{
    /// <summary>
    /// Result of attempting to delete an email from a live M365 mailbox.
    /// </summary>
    public enum MailboxDeletionResult
    {
        /// <summary>At least one matching message was found and deleted (moved to Deleted Items).</summary>
        Deleted,
        /// <summary>No matching message was found in the mailbox (e.g. it was already removed).</summary>
        NotFound,
        /// <summary>An error occurred while talking to the Graph API.</summary>
        Failed
    }

    /// <summary>
    /// Deletes a single email from a live Microsoft Graph (M365) mailbox by matching the
    /// archived email's internet message id. The local archive copy is never touched here.
    /// Deletion moves the message to the mailbox's Deleted Items folder (recoverable).
    /// </summary>
    public class GraphMailDeleter
    {
        private readonly GraphAuthClientFactory _authFactory;
        private readonly ILogger<GraphMailDeleter> _logger;

        public GraphMailDeleter(
            GraphAuthClientFactory authFactory,
            ILogger<GraphMailDeleter> logger)
        {
            _authFactory = authFactory;
            _logger = logger;
        }

        /// <summary>
        /// Finds the message matching the archived email's internet message id in the given M365
        /// mailbox and deletes it. Looks across the whole mailbox (all folders), so it works
        /// regardless of which folder the message currently lives in.
        /// </summary>
        public async Task<MailboxDeletionResult> DeleteEmailFromMailboxAsync(ArchivedEmail email, MailAccount account)
        {
            if (string.IsNullOrWhiteSpace(email.MessageId))
            {
                _logger.LogWarning(
                    "Cannot delete email {EmailId} from M365 mailbox: no internet message id is stored",
                    email.Id);
                return MailboxDeletionResult.NotFound;
            }

            try
            {
                var graphClient = _authFactory.CreateGraphClient(account);

                // The archived MessageId is stored as the InternetMessageId (Graph normally returns it
                // with angle brackets). Try the stored value as well as the bracketed/unbracketed variant
                // to be robust against how it was persisted.
                var messageIds = new List<string>();
                foreach (var candidate in BuildMessageIdCandidates(email.MessageId))
                {
                    messageIds = await FindMessageIdsByInternetMessageIdAsync(
                        graphClient, account.EmailAddress, candidate);

                    if (messageIds.Count > 0)
                        break;
                }

                if (messageIds.Count == 0)
                {
                    _logger.LogWarning(
                        "No matching message found in M365 mailbox {Mailbox} for archived email {EmailId} (MessageId={MessageId})",
                        account.EmailAddress, email.Id, email.MessageId);
                    return MailboxDeletionResult.NotFound;
                }

                bool anyDeleted = false;
                bool anyError = false;

                foreach (var msgId in messageIds)
                {
                    try
                    {
                        await graphClient.Users[account.EmailAddress].Messages[msgId].DeleteAsync();
                        anyDeleted = true;
                        _logger.LogInformation(
                            "Deleted message {MessageId} from M365 mailbox {Mailbox} for archived email {EmailId}",
                            msgId, account.EmailAddress, email.Id);
                    }
                    catch (Exception ex)
                    {
                        anyError = true;
                        _logger.LogError(ex,
                            "Error deleting message {MessageId} from M365 mailbox {Mailbox} for archived email {EmailId}",
                            msgId, account.EmailAddress, email.Id);
                    }
                }

                if (anyDeleted)
                    return MailboxDeletionResult.Deleted;

                return anyError ? MailboxDeletionResult.Failed : MailboxDeletionResult.NotFound;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error deleting archived email {EmailId} from M365 mailbox {Mailbox}",
                    email.Id, account.EmailAddress);
                return MailboxDeletionResult.Failed;
            }
        }

        /// <summary>
        /// Queries the mailbox for all messages whose internetMessageId equals the given value.
        /// Returns the Graph message ids (a message id can appear in more than one folder).
        /// </summary>
        private async Task<List<string>> FindMessageIdsByInternetMessageIdAsync(
            GraphServiceClient graphClient, string mailbox, string internetMessageId)
        {
            var ids = new List<string>();

            // Escape single quotes for the OData filter literal.
            var escaped = internetMessageId.Replace("'", "''");
            var filter = $"internetMessageId eq '{escaped}'";

            try
            {
                var response = await graphClient.Users[mailbox].Messages.GetAsync(requestConfiguration =>
                {
                    requestConfiguration.QueryParameters.Filter = filter;
                    requestConfiguration.QueryParameters.Select = new[] { "id", "internetMessageId" };
                    requestConfiguration.QueryParameters.Top = 100;
                });

                if (response?.Value != null)
                {
                    ids.AddRange(response.Value
                        .Where(m => !string.IsNullOrEmpty(m.Id))
                        .Select(m => m.Id!));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Error querying M365 mailbox {Mailbox} for internetMessageId {InternetMessageId}",
                    mailbox, internetMessageId);
            }

            return ids;
        }

        /// <summary>
        /// Builds the candidate internet message id values to search for: the stored value plus
        /// the bracketed/unbracketed counterpart.
        /// </summary>
        private static IEnumerable<string> BuildMessageIdCandidates(string messageId)
        {
            var trimmed = messageId.Trim();
            var candidates = new List<string> { trimmed };

            if (trimmed.StartsWith("<") && trimmed.EndsWith(">") && trimmed.Length > 2)
            {
                candidates.Add(trimmed.Substring(1, trimmed.Length - 2));
            }
            else if (!trimmed.StartsWith("<"))
            {
                candidates.Add($"<{trimmed}>");
            }

            return candidates.Distinct();
        }
    }
}
