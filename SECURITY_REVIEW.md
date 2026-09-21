# Multiplayer configuration audit and hardening — 1.3.9

Scope: Compost Bin source, regression tests, and relevant implementations from the locally installed Vintage Story 1.22.7 assemblies. This was a local code/test audit, not a live malicious-client session or a comprehensive security review of the game.

## Configuration authority

The server calls LoadModConfig in StartServerSide. StartClientSide does not load a local config. The server publishes its settings into its world configuration; the game serializes copies to clients in server identification/world metadata packets. A client changing its local file or its local world configuration does not change the server's copy.

The server physics coordinator uses the server-loaded config. Server block entities read server world settings for decomposition, sealing, yield, ignition, and interaction costs. No mod-defined network channel or incoming block packet deserializes a client-supplied config. The seal packet is an action request, not a saved-state/config upload; its timestamp is assigned from the server calendar.

Added regression checks confirm that client-side changes to duration, yield, ignition, and decomposition speed leave server settings unchanged. Unknown block packet data cannot inject settings, and extra settings/timestamp data attached to a seal request is ignored. All regression checks pass. This establishes the tested boundaries, not immunity to unrelated engine or server-mod vulnerabilities.

Changing the authoritative JSON requires access to the server's data directory and a world/server reload. An in-game admin designation alone does not provide a configuration editor in this mod. Other installed server-side code and privileged server administration are trusted and outside the ordinary-client boundary.

## Findings addressed in 1.3.9

### Remote barrel actions

The 1.3.5 audit found that BECompostBin.OnReceivedClientPacket checked the server side, player inventory manager, claim access, and sealed/burning state, but did not check player distance/dimension or require an open session before sealing/forwarding inventory packets.

The installed game's ServerSystemBlockSimulation.HandleBlockEntityPacket forwards messages to a loaded block entity without adding a distance check. InventoryGeneric uses InventoryBase.CanPlayerAccess, which returns true; some native inventory movement paths check open state, but the direct activation path is not a substitute for a barrel access check.

Version 1.3.9 checks the server's player position and dimension, server-configured picking range (plus the GUI's half-block slack), land claims, and barrel state. Modification and sealing require an established session. InventoryCompostBin rechecks access for native opening, activation, move/flip permissions, shift-transfer selection, and HasOpened (also used by native held-item interactions). Removal, breaking, and unloading revoke access and close existing sessions. Inventory IDs and client packet coordinates now include the dimension through InternalY; overworld IDs remain unchanged.

### Inventory payload validation

The 1.3.5 barrel forwarded every packet ID below 1000 directly to native InventoryNetworkUtil.HandleClientPacket. The native utility decoded bytes before dispatching supported inventory actions. Missing/malformed data could throw; no full server crash or denial-of-service impact was established in that audit.

Version 1.3.9 accepts only activate/move/flip inventory requests (7/8/9). A 4 KiB limit and schema preflight reject unknown or duplicate protobuf fields, invalid wire types, overflowing/truncated lengths, and invalid varints before native decoding. Typed requests then validate matching IDs, known player inventory references, both inventories' access, slot bounds, creative tabs, positive transfer quantities, and native control values. Decoder exceptions are contained before execution. Valid requests retain native execution and synchronization. Invalid-request warnings are limited to one per barrel per ten seconds and do not log payloads.

The custom network utility covers both the mod's byte-payload route and native typed requests dispatched to its inventory. Native requests dispatched through another inventory still check the compost inventory's modification permissions. This does not replace the engine's global packet decoder or harden every other inventory type.

## Privileged behavior

The native creative item-creation path requires server-granted Creative mode. Creative players can create items with attributes that affect a bin's contents/temperature; this is a powerful server-granted ability, not an ordinary survival client's config override. Server owners should treat Creative/admin access accordingly.

## Status

No direct ordinary-client configuration override was found in the examined paths. The two findings above are addressed in 1.3.9. Regression checks exercise rejected requests and legitimate native inventory execution using installed game assemblies with stubbed world/network services. A live multiplayer regression and malicious-client session remain unperformed; these checks do not establish comprehensive game security. Privileged Creative item creation and other trusted server code remain outside the ordinary-client boundary.
