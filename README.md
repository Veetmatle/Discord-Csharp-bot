# LaskBot

LaskBot is a Discord bot built with .NET 9.0. It tracks League of Legends and TFT matches, answers questions using Google Gemini AI, and runs an autonomous AI agent (OpenClaw) capable of executing code, generating charts, analysing data, and returning files directly to Discord.

The system runs as two Docker containers: the bot itself (C#) and the OpenClaw agent (Python). They communicate over an internal network via HTTP — no shared volumes.

---

## What it does

**League of Legends and TFT tracking** — register your Riot account and the bot automatically detects when you finish a match. It renders a post-game summary image (champion, KDA, CS, gold, damage, items sorted by role) and posts it to a configured channel. Both LoL and TFT are supported with separate renderers.

**AI assistant** — `/laskbot ask` sends your question to Google Gemini with optional image or document attachment. The `!ask` text command supports multiple attachments at once, including images, PDFs, DOCX, XLSX, and more.

**AI agent** — `/laskbot agent-task` submits a task to OpenClaw. The agent writes and executes code, calls external APIs, generates charts and reports, and sends the output files back to Discord. Tasks run asynchronously; you get notified when done.

**Politechnika Krakowska integration** — `/laskbot pk` scrapes the WIiT faculty website and uses Gemini to find the most relevant document (schedule, syllabus, exam results) matching your query.

---

## Commands

| Command | Description |
|---|---|
| `/laskbot register nick tag` | Register your LoL account on this server |
| `/laskbot unregister` | Remove your account from this server |
| `/laskbot setup-channel channel` | Set the notification channel (admin only) |
| `/laskbot check-latest-match` | Show your most recent LoL match |
| `/laskbot check-latest-tft` | Show your most recent TFT match |
| `/laskbot ask query [attachment]` | Ask Gemini AI with optional single attachment |
| `/laskbot agent-task prompt [pdf]` | Run a task on the AI agent |
| `/laskbot pk query` | Search WIiT Politechnika Krakowska resources |
| `/laskbot pk-watch-start` | Subscribe this channel to schedule change alerts |
| `/laskbot pk-watch-stop` | Unsubscribe this channel |
| `/laskbot status` | Show uptime, tracked players, cache stats |
| `!ask question` | Ask Gemini with multiple attachments |

---

## Quick start

```bash
cp .env.example .env
# fill in your keys
docker compose up -d
```

Required environment variables:

| Variable | Description |
|---|---|
| `DISCORD_TOKEN` | Discord bot token |
| `GEMINI_API_KEY` | Google Gemini API key |
| `RIOT_TOKEN` | Riot Games API key |
| `ANTHROPIC_API_KEY` | Anthropic API key (for OpenClaw) |

---

## Architecture

```
Discord
  |
  v
discord-bot (C# .NET 9.0)              openclaw (Python 3.12)
  |  - slash command handling             |  - Flask HTTP API
  |  - match polling loop                 |  - Claude tool_use agent
  |  - image rendering (ImageSharp)       |  - write_file / run_bash
  |  - Gemini AI calls                    |  - mark_output
  |  - agent task orchestration           |  - web_search (conditional)
  |                                       |
  |------- HTTP POST /tasks ------------->|
  |------- HTTP GET /tasks/{id} -------->|
  |<------ base64 files /tasks/{id}/files-|
```

The bot and agent run on an isolated internal Docker network (`internal: true`). The agent has a separate external network for Anthropic API calls and web access. No shared filesystem between containers.

---

## OpenClaw agent

OpenClaw exposes a simple REST API:

| Endpoint | Method | Description |
|---|---|---|
| `/tasks` | POST | Submit a task |
| `/tasks/{id}` | GET | Poll status and file metadata |
| `/tasks/{id}/files` | GET | Download output files as base64 JSON |
| `/tasks/{id}` | DELETE | Cancel a running task |
| `/health` | GET | Health check |

The agent uses Claude Sonnet via the Anthropic API with native `tool_use`. Available tools: `write_file`, `run_bash`, `read_file`, `list_dir`, `mark_output`, and `web_search` (enabled conditionally based on prompt keywords).

Files are returned as base64-encoded JSON through `GET /tasks/{id}/files`. The bot decodes them into `MemoryStream` and uploads directly to Discord. No Docker volume is shared between containers.

Pre-installed Python libraries include: matplotlib, numpy, pandas, seaborn, plotly, scipy, scikit-learn, openpyxl, pypdf, python-docx, reportlab, pillow, requests, httpx, beautifulsoup4, and more.

The agent environment also includes .NET 9.0 SDK and Node.js 20 LTS.

---

## Technical deep-dive

### Dependency injection and service lifetime

All services are registered in `ServiceCollectionExtensions.cs` using `Microsoft.Extensions.DependencyInjection`. Every service uses singleton lifetime — the bot is a long-running process and creating new instances per request would be wasteful and incorrect for stateful components like `UserRegistry`.

Services that need HTTP access are registered with `IHttpClientFactory` rather than instantiating `HttpClient` directly. This prevents socket exhaustion caused by `HttpClient` disposal and ensures DNS changes are picked up through connection pool recycling.

```
Program.cs
  └── ServiceCollection.AddApplicationServices()
        ├── AppSettings (singleton, immutable)
        ├── IUserRegistry -> UserRegistry (ReaderWriterLockSlim)
        ├── IGuildConfigRegistry -> GuildConfigRegistry (ReaderWriterLockSlim)
        ├── IGameSummaryRenderer -> ImageSharpRenderer (SemaphoreSlim)
        ├── ITftSummaryRenderer -> TftImageSharpRenderer (SemaphoreSlim)
        ├── RiotService (IHttpClientFactory + SemaphoreSlim)
        ├── GeminiService (IHttpClientFactory + SemaphoreSlim)
        ├── IAgentClient -> OpenClawAgentClient (IHttpClientFactory)
        ├── IAgentOrchestrator -> AgentOrchestrator (Channel<T>)
        └── IAgentService -> AgentService (facade)
```

Interfaces are used for `IAgentClient`, `IAgentOrchestrator`, `IAgentService`, `IUserRegistry`, `IGuildConfigRegistry`, `IGameSummaryRenderer`, and `ITftSummaryRenderer`, making each component independently testable and swappable.

---

### Polly retry policies with Retry-After support

All three HTTP clients (Riot API, Gemini, OpenClaw) share a Polly policy configured in `ServiceCollectionExtensions.GetRetryPolicy()`:

```csharp
HttpPolicyExtensions
    .HandleTransientHttpError()           // 5xx, network failures
    .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests)
    .WaitAndRetryAsync(
        retryCount: 3,
        sleepDurationProvider: (attempt, response, _) =>
        {
            // Respect Retry-After header if present (e.g. from Riot API)
            if (response.Result?.StatusCode == HttpStatusCode.TooManyRequests &&
                response.Result.Headers.RetryAfter?.Delta is { } retryAfter)
                return retryAfter;
            // Otherwise exponential backoff: 2s, 4s, 8s
            return TimeSpan.FromSeconds(Math.Pow(2, attempt));
        });
```

The policy handles 429 responses intelligently: if the API returns a `Retry-After` header, the delay uses that value exactly. Without it, exponential backoff kicks in. Every retry attempt is logged with structured properties for filtering.

---

### Multi-layer rate limiting

The application has four separate rate limiting mechanisms, each scoped to a different concern:

| Layer | Mechanism | Parameters | Purpose |
|---|---|---|---|
| `RiotService` | `SemaphoreSlim(1,1)` + timer | 1 concurrent, 1200ms min interval | Respect Riot personal API key limits |
| `GeminiService` | `SemaphoreSlim(1,1)` + timer | 1 concurrent, 1000ms min interval | Avoid Gemini burst rejections |
| `PollingStrategy` | `SemaphoreSlim(3,3)` | 3 concurrent users, 367ms stagger | Parallelise checks without hammering Riot |
| `ImageSharpRenderer` | `SemaphoreSlim(2,2)` + 30s timeout | 2 concurrent renders | Cap memory usage during image generation |

`RiotService` also maintains a backoff timestamp. When a 429 is received, it stores `DateTime.UtcNow + RetryAfter` and skips all requests until that time passes — even between polling cycles.

---

### CancellationToken propagation

A single `CancellationTokenSource` (`_shutdownCts`) is created in `BotService` at startup. Its token flows through every async operation in the application:

```
BotService._shutdownCts.Token
├── all slash command handlers
│   └── RiotService / GeminiService / AgentService calls
│       └── HttpClient.GetAsync(token) / PostAsync(token)
├── PollingStrategy (has own _cts linked via Task.Run)
│   └── PeriodicTimer.WaitForNextTickAsync(token)
│   └── ProcessSingleUserMatchAsync(token)
│       └── RiotService.GetLatestMatchIdAsync(token)
│       └── NotifyMatchFinishedAsync(token)
│           └── ImageSharpRenderer.RenderSummaryAsync(token)
│               └── LinkedTokenSource(external + 30s internal)
│                   └── RiotImageCacheService downloads(token)
└── AgentOrchestrator
    └── PollForCompletionAsync(token)
        └── Task.Delay(PollInterval, token)
```

On SIGTERM or SIGINT, `_shutdownCts.Cancel()` fires. All in-flight `HttpClient` requests, `Task.Delay` calls, and `SemaphoreSlim.WaitAsync` calls unwind via `OperationCanceledException`. `BotService.ShutdownAsync()` then stops all strategies in parallel with `Task.WhenAll`, disconnects the Discord client, and disposes the service container.

`ImageSharpRenderer` uses a linked token combining the external cancellation with a 30-second internal timeout. The render fails fast if either fires, preventing hung renders from blocking the semaphore slot.

---

### Thread-safe storage with ReaderWriterLockSlim

`UserRegistry` and `GuildConfigRegistry` store data in `Dictionary<ulong, T>` backed by JSON files. They use `ReaderWriterLockSlim` to allow multiple concurrent reads while serialising writes:

```csharp
// Multiple readers: polling, command handlers, and status queries run concurrently
_lock.EnterReadLock();
try { return _userMap.TryGetValue(id, out var account) ? account : null; }
finally { _lock.ExitReadLock(); }

// Exclusive write: registration, match ID update, unregister
_lock.EnterWriteLock();
try
{
    _userMap[discordId] = account;
    SaveInternal(); // atomic: write to .tmp, then File.Move
}
finally { _lock.ExitWriteLock(); }
```

File writes are atomic: data is serialised to a `.tmp` file first, then moved to the target path with `File.Move(..., overwrite: true)`. This means a crash mid-write never corrupts the existing file.

---

### Match polling with PeriodicTimer and bounded parallelism

`PollingStrategy` runs a background loop using `PeriodicTimer` (introduced in .NET 6), which fires every 7 minutes. Unlike `Timer`, `PeriodicTimer` is async-aware and respects `CancellationToken` directly in `WaitForNextTickAsync`.

For each tick, it checks all registered users with bounded parallelism:

```csharp
using var semaphore = new SemaphoreSlim(MaxConcurrentChecks); // 3
var tasks = new List<Task>();

foreach (var (userId, account) in users)
{
    tasks.Add(ProcessUserWithSemaphoreAsync(semaphore, userId, account, token));
    // Stagger task starts: 367ms between each (1100ms / 3)
    await Task.Delay(DelayBetweenUsers / MaxConcurrentChecks, token);
}

await Task.WhenAll(tasks);
```

The stagger between task starts prevents three simultaneous Riot API calls from firing at exactly the same millisecond. Each task then waits on the semaphore before making its API call, so at most 3 users are being checked at any given moment regardless of how many are registered.

Before every polling cycle, the strategy checks `RiotService.IsRateLimited`. If the service is in a backoff period, the entire cycle is skipped rather than accumulating queued requests.

---

### Strategy pattern for notifications

`IMatchNotification` defines two methods: `StartMonitoringAsync` and `StopMonitoringAsync`. Two implementations exist:

- `PollingStrategy` — runs the background loop described above. Automatically detects new matches.
- `CommandStrategy` — stateless, used for on-demand `/laskbot check-latest-match`. `StartMonitoringAsync` is a no-op.

Both are stored in a `List<IMatchNotification>` in `BotService`. Adding a new notification strategy (e.g. webhook-based) requires only implementing the interface and adding it to the list — no changes to `BotService` logic.

---

### Image rendering with ImageSharp

Match summary images are generated in-process using `SixLabors.ImageSharp`. The renderer builds a scoreboard-style image with two team blocks, sorted by role (TOP → JGL → MID → BOT → SUP):

- Header: VICTORY/DEFEAT, game mode, duration
- Per-player row: champion icon (32px), level badge, name, 6 item slots + trinket + quest item, KDA, CS, gold (formatted as 12.3k), damage dealt
- Tracked player row highlighted with a different background colour
- Champion and item icons loaded from a local file cache

Asset loading for all 10 players runs in parallel with `Task.WhenAll`. Icons are cached in `ConcurrentDictionary<string, string>` (path lookup). On first access, a `SemaphoreSlim(1,1)` per file path prevents duplicate downloads when two matches finish simultaneously for players sharing a champion.

The render itself is protected by `SemaphoreSlim(2,2)` with a 30-second timeout. If both slots are occupied, new renders wait up to 30 seconds before throwing `TimeoutException`. After rendering, the image is written to a `MemoryStream` as PNG and returned — no temp files on disk.

---

### Agent task orchestration with Channel

Agent tasks are queued in a `System.Threading.Channels.Channel<AgentTask>` with a bounded capacity of 20. `Channel<T>` provides structured producer-consumer flow with built-in backpressure: if the queue is full, the writer awaits rather than dropping or throwing.

```
BotService (producer)
  └── AgentOrchestrator.EnqueueTaskAsync(task)
        └── Channel.Writer.WriteAsync(task)  // blocks if full

AgentOrchestrator (consumer, single background loop)
  └── await foreach (var task in Channel.Reader.ReadAllAsync(token))
        └── ProcessSingleTaskAsync(task, token)
              ├── OpenClawAgentClient.SubmitTaskAsync()
              ├── PollForCompletionAsync() — polls every 10s
              └── OpenClawAgentClient.GetTaskFilesAsync() — on completion
```

Tasks are processed one at a time by the orchestrator loop. Each task has its own `CancellationTokenSource` with the configured timeout (default 10 minutes), linked to the application shutdown token. If the timeout fires, the orchestrator sends a cancel request to OpenClaw and delivers a timeout result to Discord.

---

### File transfer without shared volumes

Agent output files are transferred entirely over HTTP as base64-encoded JSON. When a task completes:

1. `AgentOrchestrator` calls `GET /tasks/{id}/files` on OpenClaw
2. OpenClaw reads each output file from disk and encodes it as base64
3. The C# client deserialises into `AgentFilesResponse` containing `List<AgentOutputFile>`
4. Each `AgentOutputFile` has a `GetBytes()` method: `Convert.FromBase64String(ContentBase64)`
5. `BotService` creates a `MemoryStream` from the bytes and passes it as `FileAttachment` to `channel.SendFilesAsync`

Files over 10 MB are marked `TooLarge: true` and returned without content. Files over 25 MB are noted in the Discord message with a warning. This avoids loading multi-megabyte files into memory on the bot side when they cannot be sent anyway.

No Docker volume is mounted between `discord-bot` and `openclaw`. The containers share only a named internal network.

---

### OpenClaw: native tool use loop

The agent does not parse markdown code blocks. Claude receives structured tools through the Anthropic API and calls them directly. The tool loop in `anthropic_client.py`:

```python
while tool_rounds < MAX_TOOL_ROUNDS:
    response = client.messages.create(
        model=model, max_tokens=4096,
        system=SYSTEM_PROMPT, messages=working_messages, tools=tools
    )
    if response.stop_reason == "end_turn":
        return extract_text(response), marked_outputs
    if response.stop_reason == "tool_use":
        # execute each tool call, append results as user turn
        tool_results = [execute_tool(block, workspace, marked_outputs)
                        for block in response.content if block.type == "tool_use"]
        working_messages.append({"role": "user", "content": tool_results})
```

Available tools: `write_file`, `run_bash`, `read_file`, `list_dir`, `mark_output`. Web search (`web_search_20250305`) is added to the tool list only when the prompt contains keywords like "znajdź", "aktualne", "news", "price" — checked by regex in `engine.py` before the first API call.

`mark_output` is the key tool for result delivery: the model calls it with a list of file paths when finished. Those paths are captured in `marked_outputs` and returned to `engine.py`, which uses them directly instead of scanning the workspace. If the model never calls `mark_output`, the engine falls back to `validate_output_files` and `select_output_files`.

---

### Graceful shutdown

`BotService` registers handlers for both SIGINT (`Console.CancelKeyPress`) and SIGTERM (`AppDomain.CurrentDomain.ProcessExit`). Both cancel `_shutdownCts`.

`ShutdownAsync` then runs:

```csharp
// Stop all strategies in parallel
await Task.WhenAll(_notificationStrategies.Select(s => s.StopMonitoringAsync()));
await _politechnikaWatcher.StopWatchingAsync();
await _agentService.StopAsync();   // drains Channel, cancels in-flight tasks

// Disconnect from Discord
await _client.StopAsync();
await _client.LogoutAsync();
```

`DisposeAsync` runs after: `_shutdownCts`, `DiscordSocketClient`, and the `ServiceProvider` are disposed in order. The service provider's disposal chain handles `SemaphoreSlim`, `ReaderWriterLockSlim`, and `HttpClient` instances registered as singletons.

---

## Dependencies

**Bot (C#)**

| Package | Purpose |
|---|---|
| Discord.Net 3.x | Discord WebSocket gateway and REST API |
| Microsoft.Extensions.DependencyInjection | DI container |
| Microsoft.Extensions.Http | IHttpClientFactory |
| Microsoft.Extensions.Http.Polly | Polly integration for named clients |
| Polly 8.x | Retry policies, exponential backoff |
| Mscc.GenerativeAI | Google Gemini SDK (multimodal) |
| SixLabors.ImageSharp | In-process image generation |
| HtmlAgilityPack | HTML parsing for web scraping |
| UglyToad.PdfPig | PDF text extraction |
| Serilog | Structured logging with file and console sinks |

**OpenClaw (Python)**

| Package | Purpose |
|---|---|
| Flask + Gunicorn | HTTP API server |
| anthropic | Claude API with tool use |
| matplotlib, seaborn, plotly | Chart generation |
| pandas, numpy, scipy, scikit-learn | Data analysis |
| openpyxl, pypdf, python-docx, reportlab | Document handling |
| requests, httpx, aiohttp | HTTP clients |
| beautifulsoup4, lxml | HTML parsing |
| pillow | Image processing |

---

## Project structure

```
Discord Bot AI/
├── Configuration/         AppSettings (immutable), EnvironmentConfigProvider
├── Data/                  UserRegistry, GuildConfigRegistry (ReaderWriterLockSlim + atomic JSON)
├── Infrastructure/        ServiceCollectionExtensions (DI, IHttpClientFactory, Polly)
├── Models/                DTOs for Riot, Gemini, and Agent APIs
├── Services/
│   ├── BotService.cs              Main orchestrator, lifecycle, Discord events
│   ├── RiotService.cs             Riot API (rate limiting, backoff, match data)
│   ├── GeminiService.cs           Gemini AI (multimodal, rate limiting)
│   ├── PolitechnikaService.cs     WIiT scraper + Gemini link matching
│   ├── RiotImageCacheService.cs   Champion/item icon cache (concurrent download)
│   └── Agent/
│       ├── AgentService.cs        Facade: PDF parsing, prompt sanitisation, submission
│       ├── AgentOrchestrator.cs   Channel<T> queue, polling, timeout, result delivery
│       ├── OpenClawAgentClient.cs HTTP client (submit, poll, fetch files, cancel)
│       ├── IAgentClient.cs
│       └── IAgentOrchestrator.cs
└── Strategy/
    ├── Notification/
    │   ├── IMatchNotification.cs
    │   ├── PollingStrategy.cs     PeriodicTimer, SemaphoreSlim(3,3), staggered starts
    │   └── CommandStrategy.cs     On-demand, stateless
    └── Rendering/
        ├── IGameSummaryRenderer.cs
        ├── ImageSharpRenderer.cs  LoL scoreboard, parallel asset loading
        └── TftImageSharpRenderer.cs

openclaw-agent/
├── src/
│   ├── app.py                Flask routes (/tasks, /tasks/{id}, /tasks/{id}/files)
│   ├── core/
│   │   ├── engine.py         Task lifecycle, workspace, web_search heuristic
│   │   ├── anthropic_client.py  Tool loop, tool execution, mark_output capture
│   │   └── prompts.py        System prompt with pre-installed lib list
│   └── utils/
│       ├── file_manager.py   Workspace lifecycle, FAILURE_INDICATORS, output selection
│       └── shell_executor.py Subprocess wrapper with timeout
└── Dockerfile               Multi-stage: python:3.12-slim + .NET 9.0 + Node.js 20
```

---

## Setup notes

- In the Discord Developer Portal, enable **Message Content Intent** under the Bot tab.
- Invite the bot with permissions: Send Messages, Attach Files, Use Slash Commands.
- Use `/laskbot setup-channel` in your server to configure where match notifications are posted.
- The bot registers slash commands automatically on startup and when joining new servers.
- No manual `SERVER_IDS` configuration needed — guild management is fully automatic.