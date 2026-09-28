# AGENTS.md – Lancer Nexus Gateway

## Mission

Provide a secure, stateless-capable entry point between clients and the internal cluster.

## MVP architecture baseline

- Gateway owns identity, short-lived tokens and the MySQL-backed character-persistence boundary. Coordinator owns placement and reservations; game instances own live simulation.
- Gateway initiates idempotent transfers using `Requested -> Reserved -> Prepared -> SourceFrozen -> TargetAccepted -> Committed -> SourceReleased`. The source stays authoritative until the lease changes atomically at `Committed`.
- Character writes require a monotonic MySQL `lease_version` fencing token; Redis is only transient session, chat and presence distribution.
- Gateway uses versioned `Protocol` contracts and capabilities. It never delegates password handling or authoritative placement to a game instance.

## Rules

- Never log passwords, access tokens, refresh tokens or transfer-ticket secrets.
- Passwords are accepted only at the authentication boundary and are never forwarded to game servers.
- Validate token issuer, audience, expiry, account, session, target instance and transfer-ticket state.
- Authenticate game-instance calls with distinct per-instance bearer keys and derive the caller's instance ID from that credential; never trust an unauthenticated instance ID for ticket verification or lease transfer.
- Switch character leases only after Coordinator records `TargetAccepted`; use the MySQL transfer journal for idempotent retries, then advance Coordinator to `Committed`.
- Use MySQL transactions for account and character ownership changes.
- Use idempotency keys for login, assignment and transfer operations where retries are possible.
- Do not make placement decisions independently of the Coordinator except during an explicitly documented degraded mode.
- Keep public HTTP endpoints separate from internal QUIC/service endpoints.
- Rate-limit login and transfer operations.
- Expire idle sessions using the configured idle timeout, cap them at the absolute lifetime, and revoke expired database sessions so stale character leases cannot remain active.

## Working-model escalation

- If a task requires complex reasoning beyond the current model's reliable scope, ask the user whether switching to a stronger model is desired before continuing.
- Do not switch models silently or broaden the task because a stronger model may be useful.

## Verification

Administrative queries require current SQL sessions; in-game requests also require the calling instance's active character lease. Derive instance identity from its key. Forward typed queries and Gateway-attested identity over authenticated HTTPS using a distinct Administration service secret. SQL roles/scopes/audit remain Administration-owned. Resolving a transfer session never substitutes for the active lease check.

Permission mutation requests use the same identity boundary and are re-authorized by Administration against `permissions.manage`. Snapshot/ACK routes are private to authenticated game-instance keys or active Gateway sessions; derive ACK instance IDs from the key. Never trust a caller-supplied account, session or instance identifier without checking the current session/lease.

Test invalid credentials, expired tokens, replayed tickets, Coordinator failure, duplicate requests, reconnects and full target instances.

## Nexus baseline credentials

The base topology has eight group instances (`br-01`, `bw-01`, `ew-01`, `iw-01`, `ku-01`, `li-01`, `rh-01`, `mixed-01`). Use distinct instance keys from the Scripts-generated private JSON map through Gateway:GameInstanceKeysFile; never commit or log the map. This avoids invalid shell environment names for hyphenated IDs. Gateway remains responsible for identity and leases; Coordinator owns routing over each group instance's full SystemIds set.
