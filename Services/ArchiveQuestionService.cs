using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MailArchiver.Services
{
    /// <summary>
    /// Answers free-form questions about the email archive ("Ask the archive" box on
    /// the Summaries page). Claude is given two tools - the existing full-text search
    /// and a single-email reader - and iterates over them until it can answer.
    /// The archive itself is never uploaded; only the search results Claude requests.
    /// </summary>
    public class ArchiveQuestionService : IArchiveQuestionService
    {
        private const int SearchPageSize = 25;
        private const int SnippetLength = 200;
        private const int MaxBodyChars = 6000;

        private readonly MailArchiverDbContext _context;
        private readonly Core.EmailCoreService _emailCoreService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SummaryOptions _options;
        private readonly ILogger<ArchiveQuestionService> _logger;

        public ArchiveQuestionService(
            MailArchiverDbContext context,
            Core.EmailCoreService emailCoreService,
            IHttpClientFactory httpClientFactory,
            IOptions<SummaryOptions> options,
            ILogger<ArchiveQuestionService> logger)
        {
            _context = context;
            _emailCoreService = emailCoreService;
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<ArchiveQuestionResult> AskAsync(string question, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_options.AnthropicApiKey))
            {
                return new ArchiveQuestionResult(false, string.Empty, "Summary:AnthropicApiKey is not configured.", 0);
            }

            var messages = new List<JsonNode>
            {
                new JsonObject { ["role"] = "user", ["content"] = question }
            };

            var toolCalls = 0;
            var maxIterations = Math.Clamp(_options.MaxQuestionIterations, 1, 25);

            try
            {
                for (var iteration = 0; iteration <= maxIterations; iteration++)
                {
                    // After the iteration cap, force a final answer without further tool use
                    var forceFinalAnswer = iteration == maxIterations;
                    if (forceFinalAnswer)
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = "You have reached the tool-use limit. Give your best final answer now based on what you have found so far."
                        });
                    }

                    using var response = await CallClaudeAsync(messages, allowTools: !forceFinalAnswer, cancellationToken);
                    var root = response.RootElement;

                    var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
                    var contentElement = root.GetProperty("content");

                    if (stopReason != "tool_use")
                    {
                        var answer = CollectText(contentElement);
                        if (string.IsNullOrWhiteSpace(answer))
                        {
                            return new ArchiveQuestionResult(false, string.Empty, "The model returned no answer text.", toolCalls);
                        }
                        return new ArchiveQuestionResult(true, answer.Trim(), null, toolCalls);
                    }

                    // Echo the assistant turn back unchanged, then answer every tool_use block
                    messages.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = JsonNode.Parse(contentElement.GetRawText())
                    });

                    var toolResults = new JsonArray();
                    foreach (var block in contentElement.EnumerateArray())
                    {
                        if (block.GetProperty("type").GetString() != "tool_use")
                            continue;

                        toolCalls++;
                        var toolUseId = block.GetProperty("id").GetString() ?? string.Empty;
                        var toolName = block.GetProperty("name").GetString() ?? string.Empty;
                        var input = block.GetProperty("input");

                        string resultText;
                        try
                        {
                            resultText = toolName switch
                            {
                                "search_emails" => await ExecuteSearchAsync(input, cancellationToken),
                                "get_email" => await ExecuteGetEmailAsync(input, cancellationToken),
                                _ => $"Unknown tool: {toolName}"
                            };
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(ex, "Archive question tool {Tool} failed", toolName);
                            resultText = $"Tool error: {ex.Message}";
                        }

                        toolResults.Add(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = toolUseId,
                            ["content"] = resultText
                        });
                    }

                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
                }

                return new ArchiveQuestionResult(false, string.Empty, "Tool-use iteration limit reached without an answer.", toolCalls);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Archive question failed: {Message}", ex.Message);
                return new ArchiveQuestionResult(false, string.Empty, ex.Message, toolCalls);
            }
        }

        private async Task<JsonDocument> CallClaudeAsync(List<JsonNode> messages, bool allowTools, CancellationToken cancellationToken)
        {
            var request = new JsonObject
            {
                ["model"] = _options.Model,
                ["max_tokens"] = Math.Clamp(_options.MaxOutputTokens, 500, 32000),
                ["system"] = BuildSystemPrompt(),
                // JsonNode instances are single-parent, so clone for every request
                ["messages"] = new JsonArray(messages.Select(m => m.DeepClone()).ToArray())
            };

            if (allowTools)
            {
                request["tools"] = BuildToolDefinitions();
            }

            var client = _httpClientFactory.CreateClient("Anthropic");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
            httpRequest.Headers.Add("x-api-key", _options.AnthropicApiKey);
            httpRequest.Headers.Add("anthropic-version", "2023-06-01");
            httpRequest.Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(httpRequest, cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Anthropic API returned {(int)response.StatusCode}: {TryExtractApiError(responseJson)}");
            }

            return JsonDocument.Parse(responseJson);
        }

        private string BuildSystemPrompt()
        {
            var language = string.IsNullOrWhiteSpace(_options.Language) ? "en" : _options.Language;

            return $"""
                You are an assistant that answers questions about a personal email archive.
                You cannot see the archive directly - find the relevant emails with the tools.

                Rules:
                - Use search_emails to find candidate emails, then get_email to read the promising ones.
                - Query syntax: words are combined with AND; use "double quotes" for exact phrases;
                  from:someone filters by sender, to:someone by recipient.
                - For "first/earliest mention" questions, search with sort_order=asc and look at the earliest hits.
                - If a search returns nothing, retry with fewer words, synonyms or alternative spellings before giving up.
                - Answer in the language "{language}". Be concise and factual; never invent content.
                - When you reference a specific email, cite it inline as [#ID] with its numeric id, e.g. [#123].
                - If the archive does not contain the answer, say so honestly.

                Current UTC date: {DateTime.UtcNow:yyyy-MM-dd}
                """;
        }

        private static JsonArray BuildToolDefinitions()
        {
            return new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "search_emails",
                    ["description"] = "Full-text search over the entire email archive (subject, body, sender, recipients). " +
                                      $"Returns up to {SearchPageSize} matches per page with id, date, sender, subject and a short snippet, plus the total match count.",
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Search term(s). Words are ANDed; \"quotes\" for phrases; from:/to: for sender/recipient filters." },
                            ["sort_order"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("asc", "desc"), ["description"] = "asc = oldest first (use for 'first mention' questions), desc = newest first. Default: desc." },
                            ["from_date"] = new JsonObject { ["type"] = "string", ["description"] = "Optional: only emails sent on/after this UTC date (YYYY-MM-DD)." },
                            ["to_date"] = new JsonObject { ["type"] = "string", ["description"] = "Optional: only emails sent up to this UTC date (YYYY-MM-DD)." },
                            ["page"] = new JsonObject { ["type"] = "integer", ["description"] = "1-based page number. Default: 1." }
                        },
                        ["required"] = new JsonArray("query")
                    }
                },
                new JsonObject
                {
                    ["name"] = "get_email",
                    ["description"] = "Read one email's full content (headers and plain-text body) by the id returned from search_emails.",
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["id"] = new JsonObject { ["type"] = "integer", ["description"] = "The email id." }
                        },
                        ["required"] = new JsonArray("id")
                    }
                }
            };
        }

        private async Task<string> ExecuteSearchAsync(JsonElement input, CancellationToken cancellationToken)
        {
            var query = input.TryGetProperty("query", out var q) ? q.GetString() : null;
            if (string.IsNullOrWhiteSpace(query))
                return "Error: query must not be empty.";

            var sortOrder = input.TryGetProperty("sort_order", out var so) && so.GetString() == "asc" ? "asc" : "desc";
            var page = input.TryGetProperty("page", out var p) && p.TryGetInt32(out var pv) ? Math.Max(1, pv) : 1;
            DateTime? fromDate = input.TryGetProperty("from_date", out var fd) && DateTime.TryParse(fd.GetString(), out var fdv)
                ? DateTime.SpecifyKind(fdv, DateTimeKind.Utc) : null;
            DateTime? toDate = input.TryGetProperty("to_date", out var td) && DateTime.TryParse(td.GetString(), out var tdv)
                ? DateTime.SpecifyKind(tdv, DateTimeKind.Utc) : null;

            var (emails, totalCount) = await _emailCoreService.SearchEmailsAsync(
                query, fromDate, toDate, accountId: null, folderName: null, isOutgoing: null,
                skip: (page - 1) * SearchPageSize, take: SearchPageSize,
                allowedAccountIds: null, sortBy: "SentDate", sortOrder: sortOrder);

            cancellationToken.ThrowIfCancellationRequested();

            var results = emails.Select(e => new
            {
                id = e.Id,
                sentDateUtc = e.SentDate.ToString("yyyy-MM-dd HH:mm"),
                from = e.From,
                subject = e.Subject,
                folder = e.FolderName,
                snippet = BuildSnippet(e.Body, e.HtmlBody)
            });

            return JsonSerializer.Serialize(new
            {
                totalCount,
                page,
                pageSize = SearchPageSize,
                sortOrder,
                results
            });
        }

        private async Task<string> ExecuteGetEmailAsync(JsonElement input, CancellationToken cancellationToken)
        {
            if (!input.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
                return "Error: id must be an integer.";

            var email = await _context.ArchivedEmails
                .AsNoTracking()
                .Where(e => e.Id == id)
                .Select(e => new { e.Id, e.From, e.To, e.Cc, e.Subject, e.SentDate, e.FolderName, e.HasAttachments, e.Body, e.HtmlBody })
                .FirstOrDefaultAsync(cancellationToken);

            if (email == null)
                return $"Error: email with id {id} not found.";

            var body = !string.IsNullOrWhiteSpace(email.Body)
                ? email.Body.Trim()
                : HtmlTextExtractor.ToPlainText(email.HtmlBody);
            if (body.Length > MaxBodyChars)
                body = body.Substring(0, MaxBodyChars) + " […]";

            return JsonSerializer.Serialize(new
            {
                id = email.Id,
                sentDateUtc = email.SentDate.ToString("yyyy-MM-dd HH:mm"),
                from = email.From,
                to = email.To,
                cc = email.Cc,
                subject = email.Subject,
                folder = email.FolderName,
                hasAttachments = email.HasAttachments,
                body
            });
        }

        private static string BuildSnippet(string? body, string? htmlBody)
        {
            var text = !string.IsNullOrWhiteSpace(body) ? body.Trim() : HtmlTextExtractor.ToPlainText(htmlBody);
            text = text.Replace('\n', ' ').Replace('\r', ' ');
            return text.Length <= SnippetLength ? text : text.Substring(0, SnippetLength) + "…";
        }

        private static string CollectText(JsonElement contentElement)
        {
            var sb = new StringBuilder();
            foreach (var block in contentElement.EnumerateArray())
            {
                if (block.GetProperty("type").GetString() == "text")
                    sb.AppendLine(block.GetProperty("text").GetString());
            }
            return sb.ToString();
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
    }
}
