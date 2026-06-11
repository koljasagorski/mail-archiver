using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailArchiver.Models
{
    /// <summary>
    /// An AI-generated summary of the archived emails of one period (typically 24 hours).
    /// Created by the SummaryBackgroundService (daily schedule) or on demand from the
    /// Summaries page. The structured per-topic items are stored as JSON in ItemsJson.
    /// </summary>
    public class DailySummary
    {
        public int Id { get; set; }
        public DateTime PeriodStartUtc { get; set; }
        public DateTime PeriodEndUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public int EmailCount { get; set; }

        /// <summary>Short overall overview text written by the AI.</summary>
        public string OverviewText { get; set; } = string.Empty;

        /// <summary>Serialized List&lt;SummaryItem&gt; with per-topic summaries and email references.</summary>
        public string ItemsJson { get; set; } = "[]";

        /// <summary>The AI model that produced this summary.</summary>
        public string Model { get; set; } = string.Empty;

        public bool IsSuccess { get; set; } = true;
        public string? ErrorMessage { get; set; }

        private static readonly JsonSerializerOptions ItemsJsonOptions = new(JsonSerializerDefaults.Web);

        public List<SummaryItem> GetItems()
        {
            try
            {
                return JsonSerializer.Deserialize<List<SummaryItem>>(ItemsJson, ItemsJsonOptions) ?? new List<SummaryItem>();
            }
            catch (JsonException)
            {
                return new List<SummaryItem>();
            }
        }

        public void SetItems(List<SummaryItem> items)
        {
            ItemsJson = JsonSerializer.Serialize(items, ItemsJsonOptions);
        }
    }

    /// <summary>One topic within a daily summary, referencing the emails it covers.</summary>
    public class SummaryItem
    {
        public string Title { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;

        /// <summary>One of: urgent, action, info, newsletter.</summary>
        public string Category { get; set; } = SummaryItemCategory.Info;

        public List<SummaryItemEmail> Emails { get; set; } = new();
    }

    /// <summary>Reference to an archived email shown as a direct link below a summary item.</summary>
    public class SummaryItemEmail
    {
        public int Id { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
    }

    public static class SummaryItemCategory
    {
        public const string Urgent = "urgent";
        public const string Action = "action";
        public const string Info = "info";
        public const string Newsletter = "newsletter";

        /// <summary>Display order on the summaries page: urgent topics first.</summary>
        public static int SortOrder(string? category) => category switch
        {
            Urgent => 0,
            Action => 1,
            Info => 2,
            Newsletter => 3,
            _ => 2
        };
    }
}
