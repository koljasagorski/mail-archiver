using MailArchiver.Models;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services
{
    /// <summary>
    /// Generates the AI email summary once per day at the configured time
    /// (Summary:DailyExecutionTime, server local time). The summaries are shown
    /// on the Summaries page in the web UI.
    /// </summary>
    public class SummaryBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly SummaryOptions _options;
        private readonly ILogger<SummaryBackgroundService> _logger;

        public SummaryBackgroundService(
            IServiceScopeFactory serviceScopeFactory,
            IOptions<SummaryOptions> options,
            ILogger<SummaryBackgroundService> logger)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("AI Summary Service is disabled in configuration");
                return;
            }

            if (string.IsNullOrWhiteSpace(_options.AnthropicApiKey))
            {
                _logger.LogError("AI Summary Service is enabled but 'Summary:AnthropicApiKey' is not configured. Scheduled summaries will not run.");
                return;
            }

            if (!TimeSpan.TryParse(_options.DailyExecutionTime, out var executionTime))
            {
                _logger.LogError("Invalid Summary:DailyExecutionTime format: {Time}. Expected format: HH:mm. Service will not run.", _options.DailyExecutionTime);
                return;
            }

            _logger.LogInformation("AI Summary Service scheduled daily at {Time} (model: {Model})", _options.DailyExecutionTime, _options.Model);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var nextRun = CalculateNextRunTime(executionTime);
                    var delay = nextRun - DateTime.Now;

                    _logger.LogInformation("Next AI summary scheduled for {NextRun} (in {Hours:F1} hours)", nextRun, delay.TotalHours);

                    await Task.Delay(delay, stoppingToken);

                    if (stoppingToken.IsCancellationRequested)
                        break;

                    _logger.LogInformation("Starting scheduled AI summary generation at {Time}", DateTime.Now);

                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var summaryService = scope.ServiceProvider.GetRequiredService<ISummaryService>();
                        var summary = await summaryService.GenerateAsync("SYSTEM", stoppingToken);

                        if (summary == null)
                        {
                            _logger.LogWarning("Scheduled AI summary skipped: another generation was already running");
                        }
                        else if (summary.IsSuccess)
                        {
                            _logger.LogInformation("Scheduled AI summary completed: {Count} emails summarized", summary.EmailCount);
                        }
                        else
                        {
                            _logger.LogWarning("Scheduled AI summary failed: {Error}", summary.ErrorMessage);
                        }
                    }

                    // Small delay to prevent immediate re-execution if calculating next run time has issues
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("AI Summary Service is stopping (operation cancelled)");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in AI Summary Service: {Message}", ex.Message);
                    // Wait 1 hour before retrying after an error
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
            }

            _logger.LogInformation("AI Summary Service has stopped");
        }

        private static DateTime CalculateNextRunTime(TimeSpan executionTime)
        {
            var now = DateTime.Now;
            var todayAtExecutionTime = now.Date.Add(executionTime);

            // If today's execution time has passed, schedule for tomorrow
            if (now >= todayAtExecutionTime)
            {
                return todayAtExecutionTime.AddDays(1);
            }

            return todayAtExecutionTime;
        }
    }
}
