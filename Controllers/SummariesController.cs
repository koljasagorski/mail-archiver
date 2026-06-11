using MailArchiver.Attributes;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Models.ViewModels;
using MailArchiver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace MailArchiver.Controllers
{
    /// <summary>
    /// Web UI page showing the AI-generated daily email summaries with direct
    /// links to the summarized emails. Admin-only because summaries cover the
    /// emails of all archived accounts.
    /// </summary>
    [AdminRequired]
    public class SummariesController : Controller
    {
        private const int PageSize = 10;

        private readonly MailArchiverDbContext _context;
        private readonly ISummaryService _summaryService;
        private readonly IAuthenticationService _authenticationService;
        private readonly SummaryOptions _options;
        private readonly IStringLocalizer<SharedResource> _localizer;
        private readonly ILogger<SummariesController> _logger;

        public SummariesController(
            MailArchiverDbContext context,
            ISummaryService summaryService,
            IAuthenticationService authenticationService,
            IOptions<SummaryOptions> options,
            IStringLocalizer<SharedResource> localizer,
            ILogger<SummariesController> logger)
        {
            _context = context;
            _summaryService = summaryService;
            _authenticationService = authenticationService;
            _options = options.Value;
            _localizer = localizer;
            _logger = logger;
        }

        public async Task<IActionResult> Index(int page = 1)
        {
            if (page < 1) page = 1;

            var totalCount = await _context.DailySummaries.CountAsync();
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));
            if (page > totalPages) page = totalPages;

            var summaries = await _context.DailySummaries
                .AsNoTracking()
                .OrderByDescending(s => s.CreatedAtUtc)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .ToListAsync();

            var model = new SummariesViewModel
            {
                Summaries = summaries.Select(s => new SummaryDisplayItem
                {
                    Summary = s,
                    Items = s.GetItems()
                }).ToList(),
                CurrentPage = page,
                TotalPages = totalPages,
                FeatureEnabled = _options.Enabled,
                ApiKeyConfigured = !string.IsNullOrWhiteSpace(_options.AnthropicApiKey),
                DailyExecutionTime = _options.DailyExecutionTime
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Generate()
        {
            if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.AnthropicApiKey))
            {
                TempData["ErrorMessage"] = _localizer["SummariesDisabledWarning"].Value;
                return RedirectToAction(nameof(Index));
            }

            var username = _authenticationService.GetCurrentUserDisplayName(HttpContext) ?? "Admin";
            _logger.LogInformation("Manual AI summary generation triggered by {User}", username);

            var summary = await _summaryService.GenerateAsync(username, HttpContext.RequestAborted);

            if (summary == null)
            {
                TempData["ErrorMessage"] = _localizer["SummaryAlreadyRunning"].Value;
            }
            else if (summary.IsSuccess)
            {
                TempData["SuccessMessage"] = string.Format(_localizer["SummaryGeneratedSuccess"].Value, summary.EmailCount);
            }
            else
            {
                TempData["ErrorMessage"] = string.Format(_localizer["SummaryGenerationFailed"].Value, summary.ErrorMessage);
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var summary = await _context.DailySummaries.FindAsync(id);
            if (summary != null)
            {
                _context.DailySummaries.Remove(summary);
                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = _localizer["SummaryDeleted"].Value;
            }

            return RedirectToAction(nameof(Index));
        }
    }
}
