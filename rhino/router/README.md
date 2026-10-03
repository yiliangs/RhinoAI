# Router
The Router offers a much more powerful agentic experience when using MCP servers with Rhino3D and Grasshopper 1/2. The Router is a io based MCP server that routes any MCP requests through to Rhino.

## Advantages

Having an IO based MCP Server outside of Rhino offers many distinct advantages
1. IO can connect immediately, no need to restart Claude or Sync Rhino with it
1. As the router is outside of Rhino, it can start Rhino for the user
1. And as a bonus, it can run multiple Rhino instances at once
1. The Router can handle Crash Recovery, if Rhino crashes the Agent is informed of this, and crucially, why so it can work around the crash and recover completely.
1. The Router can start Rhino 8 and/or 9 instances at the same time, so upgrading or downgrading files, plugins etc can be done.

## Concurrency

As the router is IO based, when an AI agent starts it will create a new instance of the router, which means multiple AI agents would each have their own instance with no shared state. To prevent issues with this, the router has no state inside of it, and stores all current state in a simple sqlite database. Meaning that multiple AI agents can each open their own rhinos and interact with them simultaneously.

## Strict slot mode

By default a tool call without a `slot` goes to whichever Rhino the router can find: the slot it used last, then any Rhino that announced itself (one the user opened, or one another router spawned), then one it spawns. That is convenient at a desk and dangerous when several agents share a machine, because every router reads the same state database and the same announcements, so an agent that forgets its `slot` can land in someone else's Rhino.

Pass `--strict`, or set `RHINO_MCP_STRICT=1`, to make the router serve only the Rhinos this router process spawned with `spawn_slot`:

- A call without `slot` fails with `slot_required`. Nothing is spawned or reused on its behalf.
- A `slot` this router did not spawn, including an adopted or another router's slot, fails with `slot_not_found`, as does `close_slot` on it.
- Announcements are never adopted, and `list_slots` shows only this router's own slots.

Every result, in either mode, carries a top-level `pid`: the process id of the Rhino the call acted on. `spawn_slot` returns the new Rhino's pid and `close_slot` the pid it closed, so a caller can check that every call reached the Rhino it started.

The cost is the other side of adopting nothing. If a strict router dies without closing its slots, their Rhinos keep running, and no later strict router will list or close them. With `--hidden` there is no window to close either, so note the pids `spawn_slot` returns and end a leaked Rhino by pid.

## Development notes

- The Router has 0 rhino dependencies to keep it small.
- It uses codegen to generate generic tool wrappers for itself from the rhino plugin source.
- Rhino 8/9 get a slightly different router as 8 cannot access the latest G2 due to nuget.
- The Router is published as NativeAOT on mac and a simple .exe on Win to minimize size and dependencies
- arm64/x64 for Win should work fine

## Debugging
- Ensure you close your AI agent after each router rebuild, it must restart its instance
