# 🤖 REST API & AI Assistant Integration (Claude)

Mail Archiver provides a **read-only JSON REST API** that allows external tools and AI assistants — for example [Claude](https://claude.com) — to connect to the archive and read emails. A typical use case is a **daily routine where Claude fetches the emails of the last 24 hours and writes a summary** for you.

The API is **disabled by default** and protected by a static API key.

## ⚙️ Configuration

Add the `Api` section to your `appsettings.json` or configure it via environment variables (recommended for Docker):

```json
"Api": {
  "Enabled": true,
  "ApiKey": "<your-strong-random-key>",
  "MaxPageSize": 200,
  "BodyPreviewLength": 2000
}
```

Docker Compose example:

```yaml
services:
  mailarchive-app:
    image: s1t5/mailarchiver:latest
    environment:
      # ... existing variables ...
      - Api__Enabled=true
      - Api__ApiKey=${MAIL_ARCHIVER_API_KEY}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `false` | Enables the REST API. |
| `ApiKey` | _(empty)_ | Secret key clients must present. **Must be at least 32 characters** — the application refuses to start otherwise. Generate one with `openssl rand -base64 48`. |
| `MaxPageSize` | `200` | Maximum number of emails per page for list requests. |
| `BodyPreviewLength` | `2000` | Maximum characters of the plain-text body preview in list results (`includeBody=true`). The single-email endpoint always returns the full body. |

> ⚠️ **Security notes**
>
> - The API key grants **read access to all archived emails of all accounts**. Treat it like an admin password.
> - All API requests are recorded in the [Access Log](Logs.md) under the username `API`.
> - When exposing Mail Archiver through a reverse proxy, use HTTPS so the key is never transmitted in plain text.
> - The API is read-only: it cannot modify, delete, restore, or send emails, and it never returns account credentials or attachment contents.

## 🔑 Authentication

Send the key with every request, either as Bearer token or as `X-Api-Key` header:

```bash
curl -H "Authorization: Bearer $MAIL_ARCHIVER_API_KEY" https://archive.example.com/api/v1/info
# or
curl -H "X-Api-Key: $MAIL_ARCHIVER_API_KEY" https://archive.example.com/api/v1/info
```

Responses: `401` for a missing/invalid key, `403` when the API is disabled.

## 📡 Endpoints

All timestamps are **UTC** in ISO 8601 format. All responses are JSON (camelCase).

### `GET /api/v1/info`

Connectivity check — verifies URL and API key.

```json
{ "application": "MailArchiver", "apiVersion": "v1", "serverTimeUtc": "2026-06-11T07:00:00Z" }
```

### `GET /api/v1/accounts`

Lists all archived mail accounts (no credentials or server settings are exposed).

```json
[
  { "id": 1, "name": "Private Mail", "emailAddress": "me@example.com", "provider": "IMAP", "isEnabled": true, "lastSyncUtc": "2026-06-11T06:45:00Z" }
]
```

### `GET /api/v1/emails`

Searches archived emails. Query parameters (all optional):

| Parameter | Type | Description |
|-----------|------|-------------|
| `q` | string | Full-text search term — same syntax as the web UI search (see [Mail Search Guide](Search.md)), e.g. `q=from:billing@example.com invoice`. |
| `accountId` | int | Restrict to one mail account (see `/accounts`). |
| `folder` | string | Restrict to a folder, e.g. `INBOX`. |
| `isOutgoing` | bool | `false` = received only, `true` = sent only. |
| `sinceHours` | int | Only emails sent within the last N hours — ideal for digests (`sinceHours=24`). Ignored when `from` is set. |
| `from` | datetime | Only emails sent at or after this UTC timestamp. |
| `to` | date | Only emails sent up to this date. Treated as a calendar date; the whole day is included. |
| `page` | int | 1-based page number (default `1`). |
| `pageSize` | int | Results per page (default `50`, capped by `MaxPageSize`). |
| `includeBody` | bool | Include a truncated plain-text `bodyPreview` per email (default `false`). |

```json
{
  "items": [
    {
      "id": 4711,
      "accountId": 1,
      "accountName": "Private Mail",
      "subject": "Invoice June",
      "from": "billing@example.com",
      "to": "me@example.com",
      "cc": "",
      "sentDateUtc": "2026-06-11T05:27:06Z",
      "receivedDateUtc": "2026-06-11T05:27:06Z",
      "folderName": "INBOX",
      "isOutgoing": false,
      "hasAttachments": true,
      "bodyPreview": "Hello, please find attached the invoice..."
    }
  ],
  "totalCount": 17,
  "page": 1,
  "pageSize": 50,
  "hasMore": false
}
```

Use `page`/`hasMore` to paginate through larger result sets.

### `GET /api/v1/emails/{id}`

Returns a single email with the **full plain-text body** (derived from the HTML body when no plain-text part was archived) and attachment metadata (file names, types, sizes — never the binary contents). Returns `404` if the id does not exist.

## 🤖 Daily Email Summary with Claude

With the API enabled, you can let Claude summarize your daily mail. The pattern is always the same: fetch the recent emails with `curl`, hand the JSON to Claude, get a summary back.

### Option A: Claude Code (headless) + cron

Create a script `daily-mail-summary.sh` on a machine where the [Claude Code CLI](https://code.claude.com/docs) is installed and that can reach your Mail Archiver instance:

```bash
#!/usr/bin/env bash
set -euo pipefail

ARCHIVE_URL="https://archive.example.com"   # your Mail Archiver URL
API_KEY="$MAIL_ARCHIVER_API_KEY"            # keep the key in an env var or secret store

MAILS_JSON=$(curl -sf -H "X-Api-Key: $API_KEY" \
  "$ARCHIVE_URL/api/v1/emails?sinceHours=24&includeBody=true&pageSize=100")

claude -p "Here are my emails of the last 24 hours as JSON from my mail archive.
Write me a concise summary in German:
- Group by topic, mention senders
- Highlight anything urgent or requiring action at the top
- Ignore newsletters/spam, just count them in one line at the end

$MAILS_JSON"
```

Then schedule it daily, e.g. at 7:00 via cron:

```cron
0 7 * * * /home/user/daily-mail-summary.sh | mail -s "Daily mail summary" you@example.com
```

(Instead of `mail` you can post the output to Signal, Telegram, ntfy.sh, etc.)

### Option B: Claude API directly

If you prefer calling the Claude API yourself (e.g. from n8n, Node-RED, or a small script), send the fetched JSON as user message content. Python example:

```python
import os, requests, anthropic

mails = requests.get(
    "https://archive.example.com/api/v1/emails",
    params={"sinceHours": 24, "includeBody": "true", "pageSize": 100},
    headers={"X-Api-Key": os.environ["MAIL_ARCHIVER_API_KEY"]},
    timeout=60,
).json()

client = anthropic.Anthropic()  # uses ANTHROPIC_API_KEY
message = client.messages.create(
    model="claude-sonnet-4-6",
    max_tokens=2000,
    messages=[{
        "role": "user",
        "content": "Summarize these emails of the last 24 hours. "
                   "Highlight urgent items first, group by topic:\n\n" + str(mails),
    }],
)
print(message.content[0].text)
```

### Option C: Interactive use in Claude Code

Tell Claude Code once how to reach your archive (e.g. in your project's `CLAUDE.md`):

```markdown
## Mail archive
My mail archive is reachable at https://archive.example.com.
Read access via REST API: `curl -H "X-Api-Key: $MAIL_ARCHIVER_API_KEY" "https://archive.example.com/api/v1/emails?sinceHours=24&includeBody=true"`
Full email: `/api/v1/emails/{id}`, accounts: `/api/v1/accounts`, search: `?q=...`
```

Then you can simply ask: *"Summarize my mails from the last 24 hours"* — Claude fetches and summarizes them on demand, and can drill into individual emails via `/api/v1/emails/{id}` when needed.

## 🛠️ Troubleshooting

| Symptom | Cause / Fix |
|---------|-------------|
| `403 The REST API is disabled` | Set `Api__Enabled=true` and restart the container. |
| `401 Missing or invalid API key` | Key mismatch — check for whitespace/quoting issues in your environment variable. |
| App exits at startup with an API key error | `Api:Enabled` is `true` but the key is empty or shorter than 32 characters. |
| `429 Too Many Requests` | The API shares the global rate limit (100 requests/minute per IP). Reduce request frequency or increase `pageSize` instead of paging in small chunks. |
