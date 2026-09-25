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

Gateway owns the identity/session/character schema and its lease fencing boundary. The forward-only migrations [`001_identity_and_leases.sql`](db/migrations/001_identity_and_leases.sql), [`002_refresh_token_rotation.sql`](db/migrations/002_refresh_token_rotation.sql) and [`003_character_lease_transfers.sql`](db/migrations/003_character_lease_transfers.sql) create the account/session tables, add one-time refresh-token rotation and add the idempotency journal for lease transfers. Apply them in order with the deployment migration runner after selecting the Gateway database; they are not executed automatically by the service and contain no credentials.

The Gateway uses `MySqlConnector` for account reads and session creation through `IAccountRepository`. `POST /api/v1/auth/login` verifies bcrypt hashes, persists the session nonce hash and returns a signed short-lived token. Redis is configured through `Gateway:RedisEndpoint` and is checked with a bounded PING during readiness; it remains transient infrastructure and never replaces MySQL authority. With an empty `ConnectionStrings:Gateway` setting the service registers a fail-closed repository and does not provide demo or in-memory accounts; login returns `503` until the database and signing key are configured.

The repository can read a currently valid character lease only when character ownership, active account, active session and session-to-lease ownership all match. Its transactional `CommitCharacterLeaseTransferAsync` primitive locks the character, checks active account/session ownership, requires the expected live lease version and source instance, increments the fencing version, rotates the target lease-token hash and records the transfer ID in the same MySQL transaction. The authenticated target-acceptance route now invokes this transaction after Coordinator records `TargetAccepted`; matching retries are idempotent and a reused transfer ID with different inputs is rejected. Snapshot delivery and GameServer lease enforcement remain separate work.

`CoordinatorTransferClient` wraps the Coordinator's authenticated internal prepare, lookup, state-advance, commit and abort endpoints. It applies the configured HTTPS endpoint, bearer API key and timeout, and rejects replies for a different transfer ID. The Gateway start/reservation step is public to authenticated clients; internal lifecycle transitions remain service-to-service.

The shared Protocol defines `TransferTicketClaims` with explicit MessagePack keys for transfer, session, account, character, source and target, target system, current lease version, expiry, nonce, audience and key ID. Gateway's `TransferTicketCodec` signs these claims with a separate HMAC key (`Gateway:TransferTicketSigningKey`) and caps ticket lifetime at two minutes. Target verification checks the active Coordinator transfer and atomically consumes a distinct Redis nonce.

`POST /api/v1/transfers/start` is now the authenticated reservation entry point. It binds the request to the bearer session, obtains the live source instance and fencing version from MySQL, asks Coordinator to reserve the specified target with the transfer ID and idempotency key, then returns the Coordinator endpoint and a signed ticket carrying the database-derived source/version. Set `Gateway:TransferRateLimitPerMinute` to tune its per-IP limit. An unavailable lease returns `409`, Coordinator/configuration failures return `503`, and malformed Coordinator replies return `502`.

The authenticated source instance can report `POST /api/v1/game/freeze-transfer/{transferId}` after LLServer has frozen the source. Gateway derives the source instance from its per-instance bearer key, checks that the Coordinator transfer is still `Prepared`, and advances it to `SourceFrozen`; only the bound source can do so, and retries are idempotent. The target-side ticket verification and MySQL/Coordinator target-acceptance commit sequence are available. Snapshot transport, target snapshot preparation, source-release calls from LLServer, and GameServer lease enforcement remain pending; jump-gate travel is not end-to-end ready.

Successful placement responses contain a short-lived, single-use `JoinTicket` bound to the session, account, optional character, target instance/system and endpoint. The ticket uses the separate `Gateway:JoinTicketSigningKey`; Gateway atomically consumes its nonce in Redis at `POST /api/v1/game/verify-ticket`, and the target game server must call that endpoint before accepting a client connection. It is not a replacement for the MySQL lease.

Transfer starts return a short-lived `TransferTicket` bound to the session, character, source and target instances, target system and current lease version. Configure a distinct bearer secret of at least 32 characters for each instance under `Gateway:GameInstanceKeys:<instance-id>`; these secrets identify the caller and must be delivered through the protected deployment secret store. Game instances must call all transfer API routes over HTTPS. The target game server posts the ticket to `POST /api/v1/game/verify-transfer-ticket` with its instance ID and per-instance bearer. Gateway derives the caller identity from that key, checks the requested ID, validates the ticket binding, confirms the Coordinator transfer is `SourceFrozen`, and atomically consumes a separate Redis nonce before returning admission claims.

After loading and preparing the transferred character snapshot, the authenticated target posts the same ticket plus a fresh 32-byte base64url `TargetLeaseToken` to `POST /api/v1/game/accept-transfer`. Gateway validates the target identity and Coordinator transfer, advances it to `TargetAccepted`, atomically switches the MySQL lease and increments `lease_version`, then confirms `Committed` with Coordinator. Matching retries with the same token finish a partially completed Coordinator commit; changing the token for a used transfer ID is rejected. Once Coordinator has recorded `SourceFrozen`, the target can use the signed ticket for lifecycle recovery after its two-minute expiry; the authenticated instance binding and persisted active transfer state are still required, and Redis retains the nonce key for 24 hours after ticket expiry. The target receives the committed lease version and already retains its proposed token. Configure `Gateway:CharacterLeaseLifetimeSeconds` between 60 and 86400 (default 900).

Source and target instances can poll `GET /api/v1/game/transfers/{transferId}` with their per-instance bearer. Gateway exposes only the transfer state to the two bound instances. After local player teardown, the source calls `POST /api/v1/game/release-transfer`; Gateway permits this only when the authenticated instance is the recorded source and Coordinator already reports `Committed`. That call advances `SourceReleased` and releases the reserved target slot. LLServer currently exposes in-process freeze/release hooks but does not yet call these Gateway routes; snapshot transport/preparation and lease enforcement also remain unimplemented, so jump-gate travel is not end-to-end ready.

Login and placement use separate per-client-IP fixed-window limits from `Gateway:LoginRateLimitPerMinute` and `Gateway:PlacementRateLimitPerMinute`; rejected requests receive HTTP `429` before account or Coordinator work starts.

## Client version handshake

Before login, clients POST the shared `ClientVersionHello` contract to `/api/v1/client/version`. The Gateway compares protocol, data manifest, channel, platform and minimum/latest client versions from `Gateway:ClientProtocolVersion`, `Gateway:RequiredDataManifestId`, `Gateway:ClientChannel`, `Gateway:MinimumClientVersion` and `Gateway:LatestClientVersion`. Supported and recommended versions receive a five-minute, domain-separated HMAC handshake token. The client submits it as `handshakeToken` in the login JSON; missing, invalid or expired proof receives HTTP 428 before password verification. Required updates and unsupported protocols receive no proof. This proof establishes compatibility only and grants no account or placement rights. The Gateway does not supply a download URL; the local updater uses its configured signed manifest source.

The proof is bound to the reported version, protocol, data manifest, platform and channel. Those fields are checked again at login, so a policy change can invalidate a proof that was issued minutes earlier.

`/health/live` is process liveness only. `/health/ready` fails closed until the signing key, Coordinator API key/URL, Gateway MySQL connection and Redis endpoint are configured and both `SELECT 1` and Redis `PING` succeed.
