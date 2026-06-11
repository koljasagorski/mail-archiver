using MailArchiver.Models;

namespace MailArchiver.Models.ViewModels
{
    public class SummariesViewModel
    {
        public List<SummaryDisplayItem> Summaries { get; set; } = new();
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; } = 1;
        public bool FeatureEnabled { get; set; }
        public bool ApiKeyConfigured { get; set; }
        public string DailyExecutionTime { get; set; } = string.Empty;
    }

    public class SummaryDisplayItem
    {
        public DailySummary Summary { get; set; } = null!;
        public List<SummaryItem> Items { get; set; } = new();
    }
}
