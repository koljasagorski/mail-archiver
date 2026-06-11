using MailArchiver.Attributes;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Models.Api;
using MailArchiver.Services;
using MailArchiver.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Controllers
{
    /// <summary>
    /// Read-only JSON API over the email archive, secured by a static API key
    /// (see "Api" section in appsettings.json and doc/Api.md).
    /// Designed for automation and AI assistants (e.g. a daily Claude routine
    /// that fetches recent emails and summarizes them).
    /// </summary>
    [ApiController]
    [Route("api/v1")]
    [ApiKeyRequired]
    [EnableRateLimiting("Global")]
    public class ApiController : ControllerBase
    {
        private const string ApiUsername = "API";

        private readonly MailArchiverDbContext _context;
        private readonly Services.Core.EmailCoreService _emailCoreService;
        private readonly IAccessLogService _accessLogService;
        private readonly ApiOptions _options;
        private readonly ILogger<ApiController> _logger;

        public ApiController(
            MailArchiverDbContext context,
            Services.Core.EmailCoreService emailCoreService,
            IAccessLogService accessLogService,
            IOptions<ApiOptions> options,
            ILogger<ApiController> logger)
        {
            _context = context;
            _emailCoreService = emailCoreService;
            _accessLogService = accessLogService;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>
        /// Connectivity check. Useful as first call from a client to verify URL and API key.
        /// </summary>
        [HttpGet("info")]
        public IActionResult Info()
        {
            return Ok(new
            {
                application = "MailArchiver",
                apiVersion = "v1",
                serverTimeUtc = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Lists all archived mail accounts (without any credentials or server settings).
        /// </summary>
        [HttpGet("accounts")]
        public async Task<ActionResult<List<ApiAccountDto>>> GetAccounts()
        {
            var accounts = await _context.MailAccounts
                .AsNoTracking()
                .OrderBy(a => a.Id)
                .Select(a => new ApiAccountDto
                {
                    Id = a.Id,
                    Name = a.Name,
                    EmailAddress = a.EmailAddress,
                    Provider = a.Provider.ToString(),
                    IsEnabled = a.IsEnabled,
                    LastSyncUtc = a.LastSync
                })
                .ToListAsync();

            return Ok(accounts);
        }

        /// <summary>
        /// Searches archived emails. All dates are UTC.
        /// For a daily digest use e.g. GET /api/v1/emails?sinceHours=24&amp;includeBody=true
        /// </summary>
        /// <param name="q">Full-text search term (same syntax as the web UI search).</param>
        /// <param name="accountId">Restrict to a single mail account.</param>
        /// <param name="folder">Restrict to a folder (e.g. "INBOX").</param>
        /// <param name="isOutgoing">true = only sent mails, false = only received mails.</param>
        /// <param name="from">Only emails sent at or after this UTC timestamp.</param>
        /// <param name="to">Only emails sent up to this date (inclusive, whole day).</param>
        /// <param name="sinceHours">Convenience filter: only emails of the last N hours. Ignored when "from" is set.</param>
        /// <param name="page">1-based page number.</param>
        /// <param name="pageSize">Results per page (capped by Api:MaxPageSize).</param>
        /// <param name="includeBody">Include a truncated plain-text body preview per email.</param>
        [HttpGet("emails")]
        public async Task<ActionResult<ApiPagedResult<ApiEmailListItemDto>>> GetEmails(
            string? q = null,
            int? accountId = null,
            string? folder = null,
            bool? isOutgoing = null,
            DateTime? from = null,
            DateTime? to = null,
            int? sinceHours = null,
            int page = 1,
            int pageSize = 50,
            bool includeBody = false)
        {
            if (sinceHours.HasValue && sinceHours.Value < 1)
            {
                return BadRequest(new { error = "sinceHours must be a positive number of hours." });
            }

            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 1;
            if (pageSize > _options.MaxPageSize) pageSize = _options.MaxPageSize;

            var fromUtc = from.HasValue
                ? NormalizeToUtc(from.Value)
                : sinceHours.HasValue ? DateTime.UtcNow.AddHours(-sinceHours.Value) : (DateTime?)null;
            var toUtc = to.HasValue ? NormalizeToUtc(to.Value) : (DateTime?)null;

            var (emails, totalCount) = await _emailCoreService.SearchEmailsAsync(
                q,
                fromUtc,
                toUtc,
                accountId,
                folder,
                isOutgoing,
                skip: (page - 1) * pageSize,
                take: pageSize);

            var result = new ApiPagedResult<ApiEmailListItemDto>
            {
                Items = emails.Select(e => new ApiEmailListItemDto
                {
                    Id = e.Id,
                    AccountId = e.MailAccountId,
                    AccountName = e.MailAccount?.Name,
                    Subject = e.Subject ?? string.Empty,
                    From = e.From ?? string.Empty,
                    To = e.To ?? string.Empty,
                    Cc = e.Cc ?? string.Empty,
                    SentDateUtc = e.SentDate,
                    ReceivedDateUtc = e.ReceivedDate,
                    FolderName = e.FolderName ?? string.Empty,
                    IsOutgoing = e.IsOutgoing,
                    HasAttachments = e.HasAttachments,
                    BodyPreview = includeBody ? BuildBodyPreview(e.Body, e.HtmlBody) : null
                }).ToList(),
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize,
                HasMore = page * pageSize < totalCount
            };

            await _accessLogService.LogAccessAsync(
                ApiUsername,
                AccessLogType.Search,
                searchParameters: $"API: q={q}, accountId={accountId}, folder={folder}, from={fromUtc:O}, to={toUtc:O}, page={page}, pageSize={pageSize}",
                mailAccountId: accountId);

            return Ok(result);
        }

        /// <summary>
        /// Returns a single archived email including its full plain-text body
        /// and attachment metadata (no attachment contents).
        /// </summary>
        [HttpGet("emails/{id:int}")]
        public async Task<ActionResult<ApiEmailDetailDto>> GetEmail(int id)
        {
            var email = await _context.ArchivedEmails
                .AsNoTracking()
                .Where(e => e.Id == id)
                .Select(e => new
                {
                    e.Id,
                    e.MailAccountId,
                    AccountName = e.MailAccount.Name,
                    e.MessageId,
                    e.Subject,
                    e.From,
                    e.To,
                    e.Cc,
                    e.Bcc,
                    e.SentDate,
                    e.ReceivedDate,
                    e.FolderName,
                    e.IsOutgoing,
                    e.HasAttachments,
                    e.Body,
                    e.HtmlBody,
                    Attachments = e.Attachments.Select(a => new ApiAttachmentDto
                    {
                        Id = a.Id,
                        FileName = a.FileName,
                        ContentType = a.ContentType,
                        Size = a.Size
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (email == null)
            {
                return NotFound(new { error = $"Email with id {id} not found." });
            }

            var body = !string.IsNullOrWhiteSpace(email.Body)
                ? email.Body
                : HtmlTextExtractor.ToPlainText(email.HtmlBody);

            var result = new ApiEmailDetailDto
            {
                Id = email.Id,
                AccountId = email.MailAccountId,
                AccountName = email.AccountName,
                MessageId = email.MessageId ?? string.Empty,
                Subject = email.Subject ?? string.Empty,
                From = email.From ?? string.Empty,
                To = email.To ?? string.Empty,
                Cc = email.Cc ?? string.Empty,
                Bcc = email.Bcc ?? string.Empty,
                SentDateUtc = email.SentDate,
                ReceivedDateUtc = email.ReceivedDate,
                FolderName = email.FolderName ?? string.Empty,
                IsOutgoing = email.IsOutgoing,
                HasAttachments = email.HasAttachments,
                Body = body,
                Attachments = email.Attachments
            };

            await _accessLogService.LogAccessAsync(
                ApiUsername,
                AccessLogType.Open,
                emailId: email.Id,
                emailSubject: email.Subject,
                emailFrom: email.From,
                mailAccountId: email.MailAccountId);

            return Ok(result);
        }

        /// <summary>
        /// Stores an externally generated summary so it appears on the Summaries page.
        /// Intended for routines that produce the digest with their own Claude access
        /// (e.g. a cron job using the Claude Code CLI) instead of the built-in
        /// Anthropic API integration. See doc/AiSummaries.md.
        /// </summary>
        [HttpPost("summaries")]
        public async Task<ActionResult> PostSummary([FromBody] ApiSummarySubmission submission)
        {
            var periodEnd = NormalizeToUtc(submission.PeriodEndUtc ?? DateTime.UtcNow);
            var periodStart = NormalizeToUtc(submission.PeriodStartUtc ?? periodEnd.AddHours(-24));
            if (periodStart >= periodEnd)
            {
                return BadRequest(new { error = "periodStartUtc must be before periodEndUtc." });
            }

            var items = submission.Items ?? new List<ApiSummarySubmissionItem>();
            if (items.Count > 100)
            {
                return BadRequest(new { error = "A summary may contain at most 100 items." });
            }

            // Resolve linked email ids against the archive; unknown ids are dropped silently
            var requestedIds = items
                .SelectMany(i => i.EmailIds ?? new List<int>())
                .Distinct()
                .Take(500)
                .ToList();

            var knownEmails = await _context.ArchivedEmails
                .AsNoTracking()
                .Where(e => requestedIds.Contains(e.Id))
                .Select(e => new { e.Id, e.Subject, e.From })
                .ToDictionaryAsync(e => e.Id);

            var summaryItems = items.Select(i => new SummaryItem
            {
                Title = Truncate(i.Title?.Trim() ?? string.Empty, 300),
                Text = Truncate(i.Summary?.Trim() ?? string.Empty, 4000),
                Category = SummaryItemCategory.Normalize(i.Category),
                Emails = (i.EmailIds ?? new List<int>())
                    .Distinct()
                    .Where(knownEmails.ContainsKey)
                    .Select(id => new SummaryItemEmail
                    {
                        Id = id,
                        Subject = string.IsNullOrWhiteSpace(knownEmails[id].Subject) ? $"(#{id})" : knownEmails[id].Subject,
                        From = knownEmails[id].From ?? string.Empty
                    })
                    .ToList()
            })
            .OrderBy(i => SummaryItemCategory.SortOrder(i.Category))
            .ToList();

            var summary = new DailySummary
            {
                PeriodStartUtc = periodStart,
                PeriodEndUtc = periodEnd,
                CreatedAtUtc = DateTime.UtcNow,
                EmailCount = submission.EmailCount ?? knownEmails.Count,
                OverviewText = Truncate(submission.Overview?.Trim() ?? string.Empty, 8000),
                Model = Truncate(string.IsNullOrWhiteSpace(submission.Model) ? "external" : submission.Model.Trim(), 100),
                IsSuccess = true
            };
            summary.SetItems(summaryItems);

            _context.DailySummaries.Add(summary);
            await _context.SaveChangesAsync();

            await _accessLogService.LogAccessAsync(
                ApiUsername,
                AccessLogType.Search,
                searchParameters: $"External AI summary submitted for {periodStart:O} - {periodEnd:O}: " +
                                  $"{summary.EmailCount} emails, {summaryItems.Count} items, model={summary.Model}");

            return CreatedAtAction(nameof(PostSummary), new { id = summary.Id }, new { id = summary.Id });
        }

        private static string Truncate(string value, int maxLength)
            => value.Length <= maxLength ? value : value.Substring(0, maxLength);

        private string? BuildBodyPreview(string? body, string? htmlBody)
        {
            var text = !string.IsNullOrWhiteSpace(body)
                ? body.Trim()
                : HtmlTextExtractor.ToPlainText(htmlBody);

            if (string.IsNullOrEmpty(text))
                return string.Empty;

            return text.Length <= _options.BodyPreviewLength
                ? text
                : text.Substring(0, _options.BodyPreviewLength) + " […]";
        }

        private static DateTime NormalizeToUtc(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                // Archive timestamps are stored as UTC, so treat unspecified input as UTC
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };
        }
    }
}
