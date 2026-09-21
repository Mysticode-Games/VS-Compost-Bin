# Multiplayer configuration audit — 1.3.5

Scope: Compost Bin source, regression tests, and relevant implementations from the locally installed Vintage Story 1.22.7 assemblies. This was a local code/test audit, not a live malicious-client session or a comprehensive security review of the game.

## Configuration authority

The server calls LoadModConfig in StartServerSide. StartClientSide does not load a local config. The server publishes its settings into its world configuration; the game serializes copies to clients in server identification/world metadata packets. A client changing its local file or its local world configuration does not change the server's copy.

The server physics coordinator uses the server-loaded config. Server block entities read server world settings for decomposition, sealing, yield, ignition, and interaction costs. No mod-defined network channel or incoming block packet deserializes a client-supplied config. The seal packet is an action request, not a saved-state/config upload; its timestamp is assigned from the server calendar.

Added regression checks confirm that client-side changes to duration, yield, ignition, and decomposition speed leave server settings unchanged. Unknown block packet data cannot inject settings, and extra settings/timestamp data attached to a seal request is ignored. All regression checks pass. This establishes the tested boundaries, not immunity to unrelated engine or server-mod vulnerabilities.

Changing the authoritative JSON requires access to the server's data directory and a world/server reload. An in-game admin designation alone does not provide a configuration editor in this mod. Other installed server-side code and privileged server administration are trusted and outside the ordinary-client boundary.

## Findings requiring hardening

### Remote barrel actions are not range-checked

BECompostBin.OnReceivedClientPacket checks the server side, player inventory manager, claim access, and sealed/burning state. It does not check player distance/dimension or require an open inventory session before sealing/forwarding inventory packets.

The installed game's ServerSystemBlockSimulation.HandleBlockEntityPacket forwards messages to a loaded block entity without adding a distance check. InventoryGeneric uses InventoryBase.CanPlayerAccess, which returns true; some native inventory movement paths check open state, but the direct activation path is not a substitute for a barrel access check.

Consequently, a modified client can potentially open/seal or interact with a loaded barrel remotely where claim access permits it. This does not alter the server config, but bypasses normal physical interaction. Claim denial remains enforced. Recommended fix: enforce server-side interaction range/dimension and session validation on barrel actions, including native inventory paths that can bypass the block-entity message route.

### Inventory payload validation is incomplete

The barrel forwards every packet ID below 1000 directly to native InventoryNetworkUtil.HandleClientPacket. The native utility decodes bytes before dispatching supported inventory actions. Missing/malformed data can throw rather than be rejected by the mod. No full server crash or denial-of-service impact was established in this audit.

Recommended fix: restrict to supported inventory operations, validate payload presence/size and session/access before decoding, and reject malformed packets without mutating inventory. Preserve legitimate native inventory synchronization and record useful, rate-limited diagnostics.

## Privileged behavior

The native creative item-creation path requires server-granted Creative mode. Creative players can create items with attributes that affect a bin's contents/temperature; this is a powerful server-granted ability, not an ordinary survival client's config override. Server owners should treat Creative/admin access accordingly.

## Status

No direct ordinary-client configuration override was found in the examined paths. The two access/input-validation findings above remain unpatched in 1.3.5; this audit changed tests and documentation only. A live multiplayer regression and adversarial packet test should accompany any hardening release.
