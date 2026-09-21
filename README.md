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
