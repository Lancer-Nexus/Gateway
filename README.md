# Lancer Nexus Gateway

The Gateway is the public entry point for the Lancer Nexus MMO client. It authenticates accounts, creates sessions, assigns players to game instances and coordinates controlled transfers.

## Responsibilities

- E-mail and password login
- Password verification and account policy enforcement
- Short-lived signed access tokens and session management
- Instance assignment through the Coordinator
- Group and formation affinity during placement
- Transfer-ticket creation and validation
- Rate limiting, reconnect handling and public API health endpoints

The Gateway must not run authoritative world simulation. Persistent account and character data uses MySQL; chat and short-lived presence data may use Redis.

## Runtime

The service targets Linux and the current .NET runtime. Internal service communication uses the shared contracts from `Protocol` and private authenticated channels.

## Shared Protocol

The shared contracts are checked out in the `Protocol` submodule. Update it before every local build with:

```bash
git submodule update --init --remote --merge Protocol
```

CI performs the same update before restoring and building the Gateway, so a build uses the current `Protocol/main` commit.

## Development

```bash
git submodule update --init --remote --merge Protocol
dotnet restore tests/LancerNexus.Gateway.Tests/LancerNexus.Gateway.Tests.csproj
dotnet build tests/LancerNexus.Gateway.Tests/LancerNexus.Gateway.Tests.csproj --configuration Release --no-restore --warnaserror
dotnet test tests/LancerNexus.Gateway.Tests/LancerNexus.Gateway.Tests.csproj --configuration Release --no-build
```

The service exposes liveness, readiness and protocol-capability endpoints plus `POST /api/v1/auth/login`, `POST /api/v1/auth/refresh`, protected `GET /api/v1/me`, protected `GET /api/v1/characters` and authenticated `POST /api/v1/placement/request` (with `/api/v1/placement` retained as a compatibility alias). Refresh tokens are separate opaque values, stored only as hashes and rotated atomically in MySQL. Placement requests require a valid Gateway access token whose `session_id` matches the request, then are forwarded to the Coordinator over its private HTTPS endpoint with a bearer key and idempotency key. `/api/v1/me` returns only non-sensitive token metadata and `/api/v1/characters` scopes results to the token account. The `SessionTokenCodec` signs short-lived access-token claims with HMAC-SHA256 and validates audience, expiry, nonce and key id; signing remains disabled until a protected key is configured. If token signing or the Coordinator URL/key is missing or invalid, the Gateway fails closed with `503`; it never makes a local placement decision.

## MySQL schema

Gateway owns the identity/session/character schema and its lease fencing boundary. The forward-only migrations [`001_identity_and_leases.sql`](db/migrations/001_identity_and_leases.sql) and [`002_refresh_token_rotation.sql`](db/migrations/002_refresh_token_rotation.sql) create the account/session tables and add one-time refresh-token rotation. Apply them in order with the deployment migration runner after selecting the Gateway database; they are not executed automatically by the service and contain no credentials.

The Gateway uses `MySqlConnector` for account reads and session creation through `IAccountRepository`. `POST /api/v1/auth/login` verifies bcrypt hashes, persists the session nonce hash and returns a signed short-lived token. Redis is configured through `Gateway:RedisEndpoint` and is checked with a bounded PING during readiness; it remains transient infrastructure and never replaces MySQL authority. With an empty `ConnectionStrings:Gateway` setting the service registers a fail-closed repository and does not provide demo or in-memory accounts; login returns `503` until the database and signing key are configured.

Successful placement responses contain a short-lived, single-use `JoinTicket` bound to the session, account, optional character, target instance/system and endpoint. The ticket uses the separate `Gateway:JoinTicketSigningKey`; Gateway atomically consumes its nonce in Redis at `POST /api/v1/game/verify-ticket`, and the target game server must call that endpoint before accepting a client connection. It is not a replacement for the MySQL lease.

Login and placement use separate per-client-IP fixed-window limits from `Gateway:LoginRateLimitPerMinute` and `Gateway:PlacementRateLimitPerMinute`; rejected requests receive HTTP `429` before account or Coordinator work starts.

`/health/live` is process liveness only. `/health/ready` fails closed until the signing key, Coordinator API key/URL, Gateway MySQL connection and Redis endpoint are configured and both `SELECT 1` and Redis `PING` succeed.
