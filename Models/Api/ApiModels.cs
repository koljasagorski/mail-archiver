namespace MailArchiver.Models.Api
{
    /// <summary>
    /// DTOs returned by the read-only REST API (see Controllers/ApiController.cs).
    /// Kept deliberately flat and free of internal fields (credentials, raw storage columns)
    /// so the JSON output is safe to hand to external consumers such as AI assistants.
    /// </summary>
    public class ApiPagedResult<T>
    {
        public List<T> Items { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public bool HasMore { get; set; }
    }

    public class ApiAccountDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string EmailAddress { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public DateTime LastSyncUtc { get; set; }
    }

    public class ApiEmailListItemDto
    {
        public int Id { get; set; }
        public int AccountId { get; set; }
        public string? AccountName { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public string Cc { get; set; } = string.Empty;
        public DateTime SentDateUtc { get; set; }
        public DateTime ReceivedDateUtc { get; set; }
        public string FolderName { get; set; } = string.Empty;
        public bool IsOutgoing { get; set; }
        public bool HasAttachments { get; set; }

        /// <summary>
        /// Truncated plain-text body. Only populated when includeBody=true is requested.
        /// </summary>
        public string? BodyPreview { get; set; }
    }

    public class ApiEmailDetailDto
    {
        public int Id { get; set; }
        public int AccountId { get; set; }
        public string? AccountName { get; set; }
        public string MessageId { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public string Cc { get; set; } = string.Empty;
        public string Bcc { get; set; } = string.Empty;
        public DateTime SentDateUtc { get; set; }
        public DateTime ReceivedDateUtc { get; set; }
        public string FolderName { get; set; } = string.Empty;
        public bool IsOutgoing { get; set; }
        public bool HasAttachments { get; set; }

        /// <summary>
        /// Plain-text body. Derived from the HTML body when no plain-text part was archived.
        /// </summary>
        public string Body { get; set; } = string.Empty;

        public List<ApiAttachmentDto> Attachments { get; set; } = new();
    }

    public class ApiAttachmentDto
    {
        public int Id { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long Size { get; set; }
    }
}
