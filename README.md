# Discord Bot AI

Discord bot with League of Legends match tracking and Google Gemini AI integration. Built with .NET 9.0 and designed for Docker deployment.

## Table of Contents

- [Requirements](#requirements)
- [Quick Start](#quick-start)
- [Environment Variables](#environment-variables)
- [Architecture Overview](#architecture-overview)
- [Key Technical Features](#key-technical-features)
- [Cancellation Token Flow](#cancellation-token-flow)
- [Application Flow Scenarios](#application-flow-scenarios)
- [Component Responsibilities](#component-responsibilities)
- [Security](#security)
- [Data Persistence](#data-persistence)
- [Development](#development)

---

## Requirements

- Docker and Docker Compose
- Discord Bot Token (from Discord Developer Portal)
- Google Gemini API Key
- Riot Games API Key

---

## Quick Start

### 1. Clone and configure

```bash
cp .env.example .env
nano .env  # Edit with your API keys
```

### 2. Start the bot

```bash
docker compose up -d
```

### 3. Check logs

```bash
docker compose logs -f
```

### 4. Stop the bot

```bash
docker compose down
```

---

## Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| DISCORD_TOKEN | Yes | Discord bot token |
| GEMINI_API_KEY | Yes | Google Gemini API key |
| RIOT_TOKEN | Yes | Riot Games API key |
| RIOT_VERSION | No | Data Dragon version (default: 14.2.1) |
| DATA_PATH | No | User data directory (default: /app/data) |
| CACHE_PATH | No | Image cache directory (default: /app/cache) |
| LOG_PATH | No | Log files directory (default: /app/logs) |

---

## Architecture Overview

```
Discord Bot AI/
├── Program.cs                 # Entry point, DI container setup
├── Configuration/             # Settings and environment providers
│   ├── AppSettings.cs         # Strongly-typed configuration
│   ├── EnvironmentConfigProvider.cs
│   └── IConfigurationProvider.cs
├── Infrastructure/            # Cross-cutting concerns
│   └── ServiceCollectionExtensions.cs  # DI registration, Polly policies
├── Data/                      # Persistence layer
│   ├── IUserRegistry.cs       # Interface for user-account storage
│   ├── UserRegistry.cs        # Thread-safe JSON storage (users.json)
│   ├── IGuildConfigRegistry.cs # Interface for guild configuration
│   └── GuildConfigRegistry.cs  # Thread-safe JSON storage (guilds.json)
├── Models/                    # Data transfer objects
│   ├── RiotModels.cs          # Riot API models + GuildConfig
│   ├── GeminiModels.cs        # Gemini API models
│   └── HealthStatus.cs        # Health check + CacheStats models
├── Services/                  # Core business logic
│   ├── BotService.cs          # Main orchestrator
│   ├── RiotService.cs         # Riot API client
│   ├── GeminiService.cs       # Gemini AI client
│   ├── RiotImageCacheService.cs  # Asset caching with cleanup
│   └── LoggingService.cs      # Serilog configuration
└── Strategy/                  # Strategy pattern implementations
    ├── Notification/
    │   ├── IMatchNotification.cs  # Interface
    │   ├── PollingStrategy.cs     # Background polling
    │   └── CommandStrategy.cs     # On-demand checking
    └── Rendering/
        ├── IGameSummaryRenderer.cs  # Interface
        └── ImageSharpRenderer.cs    # Image generation
```

---

## Key Technical Features

### 0. Dynamic Guild Management

The bot automatically manages guilds without requiring manual configuration:

#### Auto-Discovery
- No need to configure SERVER_IDS in .env
- Bot automatically registers slash commands when:
  - Starting up (for all connected guilds via _client.Guilds)
  - Joining a new guild (via JoinedGuild event)
- Bot cleans up guild configuration when leaving (via LeftGuild event)

#### Multi-Server Registration
- One user can register the same Riot account on multiple servers
- RiotAccount stores a list of RegisteredGuildIds (not just one)
- When a match finishes:
  - Image is rendered ONCE
  - Notification sent to ALL servers where user is registered
- Unregistering on one server does not affect other servers
- Account is fully deleted only when no servers remain

#### Dynamic Channel Configuration
- Administrators use /laskbot setup-channel to set notification channel
- Channel ID stored in guilds.json (via GuildConfigRegistry)
- No hardcoded channel names
- If channel is deleted, bot notifies admins and cleans up config

#### Slash Commands
| Command | Description |
|---------|-------------|
| /laskbot register nick:X tag:Y | Register LoL account on this server |
| /laskbot unregister | Remove account from this server |
| /laskbot setup-channel channel:X | Set notification channel (Admin only) |
| /laskbot status | Show bot uptime, stats, cache info |
| /laskbot ask query:X | Ask Gemini AI a question |
| /laskbot info | Show bot information |

#### Data Separation (SRP)
- UserRegistry: manages user-to-Riot-account mappings (users.json)
- GuildConfigRegistry: manages per-server settings (guilds.json)
- Both use ReaderWriterLockSlim for thread safety

### 1. Dependency Injection (Microsoft.Extensions.DependencyInjection)

All services are registered in ServiceCollectionExtensions.cs:
- Singleton lifetime for stateful services
- IHttpClientFactory for managed HTTP connections
- Interface-based registration for testability

```
Program.cs
  └── ServiceCollection.AddApplicationServices()
        ├── AppSettings (singleton)
        ├── IUserRegistry -> UserRegistry
        ├── IGuildConfigRegistry -> GuildConfigRegistry
        ├── RiotImageCacheService
        ├── IGameSummaryRenderer -> ImageSharpRenderer
        ├── RiotService (uses IHttpClientFactory)
        └── GeminiService (uses IHttpClientFactory)
```

### 2. IHttpClientFactory with Named Clients

HTTP clients are managed by the factory, preventing socket exhaustion:
- RiotApi - pre-configured with X-Riot-Token header
- GeminiApi - pre-configured with 30s timeout

Benefits:
- Automatic connection pooling
- DNS refresh handling
- Centralized configuration

### 3. Polly Retry Policies

Configured in ServiceCollectionExtensions.GetRetryPolicy():
- Handles transient HTTP errors (5xx, network failures)
- Handles 429 Too Many Requests
- Exponential backoff: 2s, 4s, 8s
- Respects Retry-After header from API responses
- Structured logging on each retry attempt

### 4. Rate Limiting (SemaphoreSlim)

Multiple layers of rate limiting:

| Component | Mechanism | Purpose |
|-----------|-----------|---------|
| RiotService | SemaphoreSlim(1,1) | Max 1 concurrent request, 1.2s between requests |
| GeminiService | SemaphoreSlim(1,1) | Max 1 concurrent request, 1s between requests |
| PollingStrategy | SemaphoreSlim(3,3) | Max 3 users checked concurrently |
| ImageSharpRenderer | SemaphoreSlim(2,2) | Max 2 concurrent renders |
| RiotImageCacheService | Per-file SemaphoreSlim | Prevent duplicate downloads |

### 5. Thread-Safe Registries (ReaderWriterLockSlim)

UserRegistry.cs and GuildConfigRegistry.cs use ReaderWriterLockSlim for optimal concurrency:
- Multiple concurrent readers allowed
- Exclusive access for writes
- Atomic file operations (write to .tmp, then move)

```
Read operations:  _lock.EnterReadLock()  -> Multiple allowed
Write operations: _lock.EnterWriteLock() -> Exclusive access
```

### 6. Graceful Shutdown and Disposal Strategy

BotService implements IAsyncDisposable:
- SIGTERM/SIGINT handlers registered
- CancellationToken propagated to all async operations
- All strategies stopped before Discord client logout

Disposal strategy (what needs manual Dispose):
```
Manual dispose required:
├── _shutdownCts (CancellationTokenSource) - not in DI
└── _client (DiscordSocketClient) - not in DI, needs controlled order

Automatic dispose (by ServiceProvider):
├── RiotService         -> SemaphoreSlim
├── GeminiService       -> SemaphoreSlim  
├── RiotImageCacheService -> HttpClient, SemaphoreSlim locks
├── ImageSharpRenderer  -> SemaphoreSlim
├── UserRegistry        -> ReaderWriterLockSlim
└── GuildConfigRegistry -> ReaderWriterLockSlim
```

### 7. Structured Logging (Serilog)

- Console sink with colored output
- File sink with daily rotation
- Structured properties for filtering
- Different log levels per component

### 8. Health Status and Monitoring

- BotService.GetHealthStatus() returns current state
- /laskbot status command shows:
  - Uptime
  - Tracked players count
  - Connected servers count
  - Cache statistics (file count, size in MB)
  - Rate limit status for APIs

### 9. Automatic Cache Cleanup

RiotImageCacheService manages Data Dragon assets:
- Champion and item icons cached locally
- Periodic cleanup runs every 7 days (via BotService)
- Files not accessed for 30 days are deleted
- Prevents disk bloat on long-running instances

---

## Cancellation Token Flow

### Token Sources (creators)

| Component | Token Source | Scope |
|-----------|--------------|-------|
| BotService | _shutdownCts | Application lifetime |
| PollingStrategy | _cts | Polling loop lifetime |
| ImageSharpRenderer | CancellationTokenSource(30s) + LinkedTokenSource | Per-render timeout |

### Token Flow Diagram

```
BotService._shutdownCts.Token
│
├── HandleAskCommandAsync()
│   └── GeminiService.GetAnswerAsync(token)
│       └── HttpClient.PostAsync(token)
│
├── RegisterRiotAccountAsync()
│   └── RiotService.GetAccountAsync(token)
│   └── RiotService.GetLatestMatchIdAsync(token)
│       └── HttpClient.GetAsync(token)
│
├── NotifyMatchFinishedAsync()
│   └── ImageSharpRenderer.RenderSummaryAsync(token)
│       └── LinkedTokenSource(external + 30s timeout)
│           └── RiotImageCacheService.GetChampionIconAsync(token)
│           └── RiotImageCacheService.GetItemIconAsync(token)
│               └── HttpClient.GetByteArrayAsync(token)
│
└── PollingStrategy (has own _cts)
    └── CheckMatchesInternalAsync(_cts.Token)
        └── ProcessSingleUserMatchAsync(token)
            └── RiotService.GetLatestMatchIdAsync(token)
            └── RiotService.GetMatchDetailsAsync(token)
            └── _onNewMatchFound(account, matchData, token)
                └── NotifyMatchFinishedAsync(token)
```

### LinkedTokenSource Pattern

ImageSharpRenderer combines external cancellation with internal timeout:

```csharp
using var timeoutCts = new CancellationTokenSource(30s);
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
    cancellationToken,  // external (shutdown)
    timeoutCts.Token    // internal (timeout)
);
// Operation cancelled if EITHER token fires
```

---

## Application Flow Scenarios

### Scenario 1: User Registers Account (First Server)

```
1. User executes /laskbot register nick:Player tag:EUNE on Server A
2. BotService.OnSlashCommandAsync receives command
3. BotService.RegisterRiotAccountAsync called
4. command.DeferAsync() - Discord shows "thinking..."
5. Check if user already has an account:
   - UserRegistry.GetAccount(discordId) returns null
6. RiotService.GetAccountAsync(nick, tag, token)
   - SemaphoreSlim.WaitAsync(token) - acquire rate limit lock
   - IHttpClientFactory.CreateClient("RiotApi")
   - HttpClient.GetAsync(url, token)
   - Polly policy handles retries if needed
   - Deserialize response to RiotAccount
   - SemaphoreSlim.Release() - release lock
7. RiotService.GetLatestMatchIdAsync(puuid, token)
8. UserRegistry.RegisterUser(discordId, account, guildId)
   - Creates new account with RegisteredGuildIds = [Server A]
   - ReaderWriterLockSlim.EnterWriteLock()
   - SaveInternal() - atomic write to users.json
   - ReaderWriterLockSlim.ExitWriteLock()
9. command.FollowupAsync("Account registered...")
```

### Scenario 2: Same User Registers on Second Server

```
1. User executes /laskbot register nick:Player tag:EUNE on Server B
2. BotService.RegisterRiotAccountAsync called
3. UserRegistry.GetAccount(discordId) returns existing account
4. Check if guildId already in RegisteredGuildIds:
   - Server B not in list
5. UserRegistry.RegisterUser(discordId, account, guildId)
   - Adds Server B to existing account's RegisteredGuildIds
   - NO API call to Riot (account already known)
6. Account now has RegisteredGuildIds = [Server A, Server B]
7. command.FollowupAsync("Your account is now also tracked on this server")
```

### Scenario 3: Match Notification (Multi-Server)

```
1. PollingStrategy detects new match for user
2. NotifyMatchFinishedAsync(account, matchData, token) called
3. account.RegisteredGuildIds = [Server A, Server B]
4. Render image ONCE:
   - ImageSharpRenderer.RenderSummaryAsync(account, matchData, token)
   - Result copied to byte[] imageData
5. For each guild in RegisteredGuildIds:
   a. Server A:
      - Get channelId from GuildConfigRegistry.GetNotificationChannel(guildA)
      - Send image to channel
   b. Server B:
      - Get channelId from GuildConfigRegistry.GetNotificationChannel(guildB)
      - Send same image to channel
6. One render, multiple sends = efficient
```

### Scenario 4: User Unregisters from One Server

```
1. User executes /laskbot unregister on Server A
2. User has RegisteredGuildIds = [Server A, Server B]
3. BotService.UnregisterRiotAccountAsync called
4. UserRegistry.RemoveUserFromGuild(discordId, guildA)
   - Removes Server A from list
   - RegisteredGuildIds = [Server B]
   - Account still exists (list not empty)
5. command.FollowupAsync("Unregistered from this server. Still tracked on 1 other server(s)")

If user then unregisters from Server B:
   - RegisteredGuildIds becomes empty
   - Account fully deleted from users.json
   - command.FollowupAsync("Account completely unregistered")
```

### Scenario 5: Polling Detects New Match (Full Flow)

```
1. PollingStrategy.StartMonitoringAsync running in background
2. PeriodicTimer fires every 10 minutes
3. CheckMatchesInternalAsync(token) called
4. Check if RiotService.IsRateLimited - skip if true
5. UserRegistry.GetAllTrackedUsers() - snapshot with read lock
6. For each user (max 3 concurrent via SemaphoreSlim):
   a. Wait 500ms between task starts (1500ms / 3)
   b. ProcessUserWithSemaphoreAsync:
      - SemaphoreSlim.WaitAsync(token)
      - RiotService.GetLatestMatchIdAsync(puuid, token)
      - Compare with stored LastMatchId
      - If different: RiotService.GetMatchDetailsAsync(matchId, token)
      - UserRegistry.UpdateLastMatchId(userId, matchId)
      - Invoke _onNewMatchFound callback
      - SemaphoreSlim.Release()
7. NotifyMatchFinishedAsync(account, matchData, token)
8. ImageSharpRenderer.RenderSummaryAsync(account, matchData, token)
   - Create LinkedTokenSource (external + 30s timeout)
   - SemaphoreSlim.WaitAsync(timeout) - max 2 concurrent renders
   - RiotImageCacheService.GetChampionIconAsync(name, token)
     - Check ConcurrentDictionary cache
     - If not cached: acquire per-file lock, download, save
   - Same for items
   - Generate image with ImageSharp
   - SaveAsPngAsync(stream, token)
   - SemaphoreSlim.Release()
9. Discord channel.SendFileAsync(imageStream, ...)
```

### Scenario 6: User Asks AI Question

```
1. User executes /laskbot ask query:"What is League of Legends?"
2. BotService.HandleAskCommandAsync called
3. command.DeferAsync() - Discord shows "thinking..."
4. GeminiService.GetAnswerAsync(question, token)
   - SemaphoreSlim.WaitAsync(token) - rate limiting
   - Check minimum interval (1s)
   - Build GeminiRequest with prompt prefix
   - IHttpClientFactory.CreateClient("GeminiApi")
   - HttpClient.PostAsync(url, content, token)
   - Polly handles 429/5xx with retries
   - Parse response, extract answer text
   - SemaphoreSlim.Release()
5. command.FollowupAsync(formatted response)
```

### Scenario 7: Graceful Shutdown (Docker stop)

```
1. Docker sends SIGTERM to container
2. AppDomain.CurrentDomain.ProcessExit event fires
3. BotService._shutdownCts.Cancel() called
4. All operations with CancellationToken throw OperationCanceledException:
   - Active HTTP requests cancelled
   - Task.Delay interrupted
   - SemaphoreSlim.WaitAsync interrupted
5. Main RunAsync catches OperationCanceledException
6. ShutdownAsync() called:
   - PollingStrategy.StopMonitoringAsync() - cancels polling loop
   - CommandStrategy.StopMonitoringAsync() - no-op
   - DiscordClient.StopAsync()
   - DiscordClient.LogoutAsync()
7. DisposeAsync() called:
   - All services disposed
   - DI container disposed
8. LoggingService.Shutdown() - flush logs
9. Process exits with code 0
```

---

## Component Responsibilities

| Component | Responsibility |
|-----------|---------------|
| Program.cs | Entry point, DI setup, logging init |
| AppSettings | Strongly-typed config from environment |
| EnvironmentConfigProvider | Reads environment variables |
| ServiceCollectionExtensions | DI registration, HTTP client config, Polly policies |
| BotService | Main orchestrator, Discord event handling, lifecycle |
| RiotService | Riot API communication, rate limiting |
| GeminiService | Gemini AI communication, rate limiting |
| RiotImageCacheService | Download and cache game assets |
| UserRegistry | Persist user-account mappings, thread-safe |
| LoggingService | Serilog configuration |
| PollingStrategy | Background match detection loop |
| CommandStrategy | On-demand match checking - implemented on command (also implemented with CancellationToken, this time given - no loop) |
| ImageSharpRenderer | Generate match summary images |

---

## Security

### Docker Security

- Container runs as non-root user (uid 1000)
- Read-only root filesystem
- Tmpfs for temporary files
- Resource limits (memory, CPU)
- No privileged mode

### API Key Protection

- Keys passed via environment variables
- Never logged (Serilog configured to exclude)
- .env file excluded from version control (.gitignore)
- Docker secrets support possible

### Network Security

- HTTPS for all external API calls
- No exposed ports (bot connects outbound only)
- Discord WebSocket secured by library

### File System Security

- Atomic file writes (temp file + move)
- Per-file locks prevent race conditions
- Volume permissions set correctly

---

## Data Persistence

Docker volumes used for persistent data:

| Volume | Mount Point | Purpose |
|--------|-------------|---------|
| discord-bot-data | /app/data | User registrations (users.json) |
| discord-bot-cache | /app/cache | Downloaded champion/item icons |
| discord-bot-logs | /app/logs | Application log files |

Data survives container restarts and updates.

---

## Development

### Run locally (without Docker)

```bash
# Set required environment variables
export DISCORD_TOKEN=your_token
export GEMINI_API_KEY=your_key
export RIOT_TOKEN=your_key
export SERVER_IDS=123456789

# Run
cd "Discord Bot AI"
dotnet run
```

### Build Docker image manually

```bash
docker build -t discord-bot-ai .
```

### Project Dependencies

- Discord.Net 3.19.0 - Discord API client
- Microsoft.Extensions.DependencyInjection - DI container
- Microsoft.Extensions.Http - IHttpClientFactory
- Microsoft.Extensions.Http.Polly - Polly integration
- Polly 8.5.1 - Resilience policies
- Serilog - Structured logging
- SixLabors.ImageSharp - Image generation
- Newtonsoft.Json - JSON serialization

---

## Current Application State Summary

The application is a production-ready Discord bot with:

1. Full Docker support with graceful shutdown handling
2. Dependency Injection for loose coupling and testability
3. IHttpClientFactory for proper HTTP lifecycle management
4. Polly retry policies with exponential backoff
5. Multi-layer rate limiting to respect API limits
6. Thread-safe data storage with ReaderWriterLockSlim
7. CancellationToken propagation through entire call stack
8. Structured logging with Serilog
9. Strategy pattern for notification mechanisms
10. Image rendering with concurrency control
11. Asset caching with concurrent-safe downloads

Have fun testing! To get everything running smoothly, remember these final steps:
* Discord Developer Portal: Create a new application and a bot account at the Discord Developer Portal.
* Enable Message Intent: In the Bot tab, you MUST enable the Message Content Intent—the bot requires this to recognize and process your slash commands.
* Invite to Server: Use the OAuth2 URL generator to invite the bot to your server. Ensure it has permissions to Send Messages, Attach Files, and Use Slash Commands.
* The "bot" Channel: Create a text channel named exactly "bot" on your server. The bot specifically looks for this name to post match notifications.
* Provide correct .env for docker or params if started locally.

Enjoy the climb! :D
