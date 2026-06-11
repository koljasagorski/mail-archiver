using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace MailArchiver.Services
{
    /// <summary>
    /// Generates AI summaries of recently archived emails by calling the Anthropic
    /// (Claude) Messages API and stores them as DailySummary rows for the Summaries page.
    /// </summary>
    public class SummaryService : ISummaryService
    {
        // Prevents the scheduler and the "generate now" button from running concurrently
        private static readonly SemaphoreSlim GenerationLock = new(1, 1);

        private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

        private readonly MailArchiverDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SummaryOptions _options;
        private readonly IAccessLogService _accessLogService;
        private readonly ILogger<SummaryService> _logger;

        public SummaryService(
            MailArchiverDbContext context,
            IHttpClientFactory httpClientFactory,
            IOptions<SummaryOptions> options,
            IAccessLogService accessLogService,
            ILogger<SummaryService> logger)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _accessLogService = accessLogService;
            _logger = logger;
        }

        public async Task<DailySummary?> GenerateAsync(string triggeredBy, CancellationToken cancellationToken = default)
        {
            if (!await GenerationLock.WaitAsync(0, cancellationToken))
            {
                _logger.LogWarning("Summary generation requested by {TriggeredBy} but another generation is already running", triggeredBy);
                return null;
            }

            try
            {
                var periodEnd = DateTime.UtcNow;
                var periodStart = periodEnd.AddHours(-Math.Max(1, _options.PeriodHours));

                var summary = new DailySummary
                {
                    PeriodStartUtc = periodStart,
                    PeriodEndUtc = periodEnd,
                    CreatedAtUtc = DateTime.UtcNow,
                    Model = _options.Model
                };

                try
                {
                    var emails = await LoadEmailsAsync(periodStart, periodEnd, cancellationToken);
                    summary.EmailCount = emails.Count;

                    if (emails.Count == 0)
                    {
                        _logger.LogInformation("Summary generation: no emails in period {Start} - {End}, storing empty summary", periodStart, periodEnd);
                    }
                    else
                    {
                        var responseText = await CallClaudeAsync(BuildPrompt(emails), cancellationToken);
                        ApplyModelResponse(summary, responseText, emails);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Summary generation failed: {Message}", ex.Message);
                    summary.IsSuccess = false;
                    summary.ErrorMessage = ex.Message;
                }

                _context.Add(summary);
                await _context.SaveChangesAsync(cancellationToken);

                await _accessLogService.LogAccessAsync(
                    triggeredBy,
                    AccessLogType.Search,
                    searchParameters: $"AI summary generated for {summary.PeriodStartUtc:O} - {summary.PeriodEndUtc:O}: " +
                                      $"{summary.EmailCount} emails, success={summary.IsSuccess}");

                return summary;
            }
            finally
            {
                GenerationLock.Release();
            }
        }

        private sealed record EmailForSummary(int Id, string From, string To, string Subject,
            DateTime SentDate, string FolderName, bool IsOutgoing, bool HasAttachments, string BodyText);

        private async Task<List<EmailForSummary>> LoadEmailsAsync(DateTime periodStart, DateTime periodEnd, CancellationToken cancellationToken)
        {
            var maxEmails = Math.Clamp(_options.MaxEmails, 1, 500);

            var rows = await _context.ArchivedEmails
                .AsNoTracking()
                .Where(e => e.SentDate >= periodStart && e.SentDate <= periodEnd)
                .OrderByDescending(e => e.SentDate)
                .Take(maxEmails)
                .Select(e => new
                {
                    e.Id,
                    e.From,
                    e.To,
                    e.Subject,
                    e.SentDate,
                    e.FolderName,
                    e.IsOutgoing,
                    e.HasAttachments,
                    e.Body,
                    e.HtmlBody
                })
                .ToListAsync(cancellationToken);

            var maxBodyChars = Math.Clamp(_options.MaxBodyCharsPerEmail, 100, 20000);

            return rows.Select(e =>
            {
                var text = !string.IsNullOrWhiteSpace(e.Body)
                    ? e.Body.Trim()
                    : HtmlTextExtractor.ToPlainText(e.HtmlBody);
                if (text.Length > maxBodyChars)
                    text = text.Substring(0, maxBodyChars) + " […]";

                return new EmailForSummary(e.Id, e.From ?? "", e.To ?? "", e.Subject ?? "",
                    e.SentDate, e.FolderName ?? "", e.IsOutgoing, e.HasAttachments, text);
            }).ToList();
        }

        private string BuildPrompt(List<EmailForSummary> emails)
        {
            var emailsJson = JsonSerializer.Serialize(emails.Select(e => new
            {
                id = e.Id,
                from = e.From,
                to = e.To,
                subject = e.Subject,
                sentDateUtc = e.SentDate,
                folder = e.FolderName,
                isOutgoing = e.IsOutgoing,
                hasAttachments = e.HasAttachments,
                body = e.BodyText
            }), WebJsonOptions);

            var language = string.IsNullOrWhiteSpace(_options.Language) ? "en" : _options.Language;

            return $$"""
                You are the daily digest writer of a personal email archive. Below are the archived
                emails of the last {{Math.Max(1, _options.PeriodHours)}} hours as a JSON array.

                Write a digest in the language "{{language}}" and respond with ONLY a JSON object
                (no markdown fences, no extra text) in exactly this shape:

                {
                  "overview": "2-3 sentences summarizing the period overall",
                  "items": [
                    {
                      "title": "short topic title",
                      "summary": "1-3 sentence summary of this topic",
                      "category": "urgent|action|info|newsletter",
                      "emailIds": [123, 456]
                    }
                  ]
                }

                Rules:
                - Group related emails into one item (e.g. an email thread or several mails on one topic).
                - category "urgent": needs immediate attention. "action": needs a reply or action soon.
                  "info": worth knowing, no action needed. "newsletter": newsletters, promotions, automated notifications.
                - Put the most important items first.
                - emailIds must only contain ids that appear in the input data.
                - Combine all newsletters/promotions into a single item at the end unless one of them is genuinely important.
                - Do not invent content that is not supported by the emails.

                Emails:
                {{emailsJson}}
                """;
        }

        private async Task<string> CallClaudeAsync(string prompt, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_options.AnthropicApiKey))
                throw new InvalidOperationException("Summary:AnthropicApiKey is not configured.");

            var client = _httpClientFactory.CreateClient("Anthropic");

            var requestBody = JsonSerializer.Serialize(new
            {
                model = _options.Model,
                max_tokens = Math.Clamp(_options.MaxOutputTokens, 500, 32000),
                messages = new[] { new { role = "user", content = prompt } }
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
            request.Headers.Add("x-api-key", _options.AnthropicApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorDetail = TryExtractApiError(responseJson);
                throw new InvalidOperationException($"Anthropic API returned {(int)response.StatusCode}: {errorDetail}");
            }

            using var doc = JsonDocument.Parse(responseJson);
            var contentBlocks = doc.RootElement.GetProperty("content");
            foreach (var block in contentBlocks.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text")
                    return block.GetProperty("text").GetString() ?? string.Empty;
            }

            throw new InvalidOperationException("Anthropic API response contained no text content.");
        }

        private static string TryExtractApiError(string responseJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("error", out var error) &&
                    error.TryGetProperty("message", out var message))
                {
                    return message.GetString() ?? responseJson;
                }
            }
            catch (JsonException)
            {
                // fall through to raw response
            }
            return responseJson.Length > 500 ? responseJson.Substring(0, 500) : responseJson;
        }

        private void ApplyModelResponse(DailySummary summary, string responseText, List<EmailForSummary> emails)
        {
            var json = StripMarkdownFences(responseText);
            var emailsById = emails.ToDictionary(e => e.Id);

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                summary.OverviewText = root.TryGetProperty("overview", out var overview)
                    ? overview.GetString() ?? string.Empty
                    : string.Empty;

                var items = new List<SummaryItem>();
                if (root.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var itemElement in itemsElement.EnumerateArray())
                    {
                        var item = new SummaryItem
                        {
                            Title = itemElement.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                            Text = itemElement.TryGetProperty("summary", out var text) ? text.GetString() ?? "" : "",
                            Category = SummaryItemCategory.Normalize(itemElement.TryGetProperty("category", out var cat) ? cat.GetString() : null)
                        };

                        if (itemElement.TryGetProperty("emailIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var idElement in ids.EnumerateArray())
                            {
                                // Only link emails that were actually part of the input
                                if (idElement.TryGetInt32(out var id) && emailsById.TryGetValue(id, out var email))
                                {
                                    item.Emails.Add(new SummaryItemEmail
                                    {
                                        Id = email.Id,
                                        Subject = string.IsNullOrWhiteSpace(email.Subject) ? $"(#{email.Id})" : email.Subject,
                                        From = email.From
                                    });
                                }
                            }
                        }

                        items.Add(item);
                    }
                }

                summary.SetItems(items
                    .OrderBy(i => SummaryItemCategory.SortOrder(i.Category))
                    .ToList());
            }
            catch (JsonException ex)
            {
                // The model did not return valid JSON: keep the raw text as overview so nothing is lost
                _logger.LogWarning(ex, "Summary response was not valid JSON, storing raw text");
                summary.OverviewText = responseText.Trim();
            }
        }

        private static string StripMarkdownFences(string text)
        {
            var trimmed = text.Trim();
            if (trimmed.StartsWith("```"))
            {
                var firstLineEnd = trimmed.IndexOf('\n');
                if (firstLineEnd >= 0)
                    trimmed = trimmed.Substring(firstLineEnd + 1);
                var fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                if (fenceEnd >= 0)
                    trimmed = trimmed.Substring(0, fenceEnd);
            }
            return trimmed.Trim();
        }
    }
}
