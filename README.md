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

The service exposes liveness, readiness and protocol-capability endpoints plus `POST /api/v1/auth/login`, protected `GET /api/v1/me` and authenticated `POST /api/v1/placement/request` (with `/api/v1/placement` retained as a compatibility alias). Placement requests require a valid Gateway access token whose `session_id` matches the request, then are forwarded to the Coordinator over its private HTTPS endpoint with a bearer key and idempotency key. `/api/v1/me` returns only non-sensitive token metadata. The `SessionTokenCodec` signs short-lived access-token claims with HMAC-SHA256 and validates audience, expiry, nonce and key id; signing remains disabled until a protected key is configured. If token signing or the Coordinator URL/key is missing or invalid, the Gateway fails closed with `503`; it never makes a local placement decision.

## MySQL schema

Gateway owns the identity/session/character schema and its lease fencing boundary. The forward-only migration in [`db/migrations/001_identity_and_leases.sql`](db/migrations/001_identity_and_leases.sql) creates accounts, sessions, characters and `lease_version`-based character leases. Apply it with the deployment migration runner after selecting the Gateway database; it is not executed automatically by the service and contains no credentials.

The Gateway uses `MySqlConnector` for account reads and session creation through `IAccountRepository`. `POST /api/v1/auth/login` verifies bcrypt hashes, persists the session nonce hash and returns a signed short-lived token. With an empty `ConnectionStrings:Gateway` setting the service registers a fail-closed repository and does not provide demo or in-memory accounts; login returns `503` until the database and signing key are configured.
