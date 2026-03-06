---
name: minecraft-guide
description: Optimizes tool calls for Minecraft Agent by pairing with pre-existing MCP tools, providing best practices, sequences, and wrappers for efficient automation.
---

# Minecraft Guide Skill

This skill provides optimized patterns for working with the Minecraft Agent and its MCP tools. Use this skill when automating Minecraft gameplay, building structures, exploring worlds, or performing any game-related tasks through AI agents.

## 1. Full Tool List Summary

| Tool Name | Category | Purpose |
|-----------|----------|---------|
| `MinecraftMCP_get-position` | Player | Get current player coordinates |
| `MinecraftMCP_list-inventory` | Player | Get full player inventory contents |
| `MinecraftMCP_equip-item` | Player | Equip an item to hand or armor slot |
| `MinecraftMCP_detect-gamemode` | World Info | Detect current gamemode (survival, creative, etc.) |
| `MinecraftMCP_get-block-info` | World | Get block type and properties at specific coordinates |
| `MinecraftMCP_find-block` | Search | Find blocks of a specific type nearby |
| `MinecraftMCP_find-entity` | Search | Find entities of a specific type nearby |
| `MinecraftMCP_find-item` | Search | Find item drops of a specific type nearby |
| `MinecraftMCP_move-to-position` | Movement | Move to coordinates (survival/adventure) |
| `MinecraftMCP_fly-to` | Movement | Fly to coordinates (creative/spectator) |
| `MinecraftMCP_move-in-direction` | Movement | Move in a relative direction |
| `MinecraftMCP_look-at` | Camera | Look at specific coordinates |
| `MinecraftMCP_jump` | Actions | Make the player jump |
| `MinecraftMCP_dig-block` | Building | Break a block at specific coordinates |
| `MinecraftMCP_place-block` | Building | Place a block at specific coordinates |
| `MinecraftMCP_can-craft` | Crafting | Check if an item can be crafted with current inventory |
| `MinecraftMCP_craft-item` | Crafting | Craft an item |
| `MinecraftMCP_get-recipe` | Crafting | Get the recipe for a specific item |
| `MinecraftMCP_list-recipes` | Crafting | List all available recipes |
| `MinecraftMCP_smelt-item` | Crafting | Smelt an item in a furnace |
| `MinecraftMCP_read-chat` | Communication | Read recent chat messages |
| `MinecraftMCP_send-chat` | Communication | Send a chat message or run a server command |

---

## 2. Optimized Call Patterns

### Building a Structure

1. Call `MinecraftMCP_detect-gamemode` to confirm context
2. Call `MinecraftMCP_get-position` to establish starting coordinates
3. Call `MinecraftMCP_get-block-info` on target positions to verify they are clear
4. Call `MinecraftMCP_place-block` for each position, working bottom-up layer by layer
5. Call `MinecraftMCP_send-chat` to log completion status

### Exploring / Navigating

1. Call `MinecraftMCP_detect-gamemode` — use `MinecraftMCP_fly-to` in creative, `MinecraftMCP_move-to-position` in survival
2. Call `MinecraftMCP_look-at` on the destination for orientation
3. Call `MinecraftMCP_find-block` or `MinecraftMCP_find-entity` to scan surroundings on arrival
4. Use `MinecraftMCP_move-in-direction` for small positional adjustments

### Mining Resources

1. Call `MinecraftMCP_equip-item` with the appropriate pickaxe before starting
2. Call `MinecraftMCP_find-block` to locate the target ore
3. Call `MinecraftMCP_move-to-position` to navigate to the block
4. Call `MinecraftMCP_look-at` on the block coordinates
5. Call `MinecraftMCP_dig-block` to break it
6. Call `MinecraftMCP_find-item` to confirm the drop exists

### Crafting

1. Call `MinecraftMCP_list-inventory` to assess available materials
2. Call `MinecraftMCP_can-craft` to check feasibility
3. If it fails, call `MinecraftMCP_get-recipe` to identify what is missing
4. Smelt any raw materials first using `MinecraftMCP_smelt-item`
5. Call `MinecraftMCP_craft-item` once materials are ready
6. Call `MinecraftMCP_equip-item` to use the crafted item immediately

---

## 3. Example Task Walkthroughs

### Build a House

1. Call `MinecraftMCP_detect-gamemode` and `MinecraftMCP_get-position` to establish starting context
2. Call `MinecraftMCP_place-block` with `cobblestone` across a 10x10 grid one layer below player Y for the foundation
3. Call `MinecraftMCP_place-block` with `oak_planks` along the perimeter up 7 blocks for walls
4. Call `MinecraftMCP_dig-block` on two adjacent wall blocks at ground level for a door opening
5. Call `MinecraftMCP_place-block` with `oak_stairs` along the roof perimeter
6. Call `MinecraftMCP_send-chat` to confirm completion

### Find and Mine Diamonds

1. Call `MinecraftMCP_equip-item` with `diamond_pickaxe`
2. Call `MinecraftMCP_find-block` for `diamond_ore`
3. If none found, call `MinecraftMCP_move-in-direction` to reposition and retry
4. Call `MinecraftMCP_move-to-position` to reach the target block
5. Call `MinecraftMCP_look-at` on the block, then `MinecraftMCP_dig-block`
6. Call `MinecraftMCP_find-item` to confirm the diamond drop
7. Call `MinecraftMCP_send-chat` to log the find location

### Craft and Gear Up

1. Call `MinecraftMCP_list-inventory` to see what raw materials are available
2. Call `MinecraftMCP_smelt-item` on any `raw_iron` or `raw_gold` as needed
3. Call `MinecraftMCP_can-craft` for the target item (e.g. `iron_chestplate`)
4. If it fails, call `MinecraftMCP_get-recipe` to see what is missing
5. Call `MinecraftMCP_craft-item` once ready
6. Call `MinecraftMCP_equip-item` to put it on

### Set Up a Mob Farm

1. Call `MinecraftMCP_detect-gamemode` then navigate to the spawn area using the correct movement tool
2. Call `MinecraftMCP_place-block` with `cobblestone` to build a 10x10 dark platform
3. Call `MinecraftMCP_place-block` with `water` at the centre collection point
4. Call `MinecraftMCP_place-block` with `hopper` at the corners of the collection area
5. Call `MinecraftMCP_look-at` the platform centre to visually verify the layout
6. Call `MinecraftMCP_send-chat` to log completion

---

## 4. Common Commands via send-chat

| Command | Action |
|---------|--------|
| `/time set day` | Set time to day |
| `/time set night` | Set time to night |
| `/weather clear` | Clear weather |
| `/give @p diamond 64` | Give 64 diamonds |
| `/tp x y z` | Teleport to coordinates |
| `/spawnpoint` | Set spawn point |
| `/gamerule doMobSpawning false` | Disable mob spawning |
| `/kill @e` | Kill all entities |

---

## 5. Error Handling

**Navigation fails**
- Confirm gamemode with `MinecraftMCP_detect-gamemode` and use the correct tool (`fly-to` vs `move-to-position`)
- Fall back to `MinecraftMCP_send-chat` with a `/tp` command if pathfinding is blocked

**Block placement fails**
- Call `MinecraftMCP_get-block-info` to verify the space is clear before placing
- Ensure the correct item is equipped with `MinecraftMCP_equip-item`

**Crafting fails**
- Always gate `MinecraftMCP_craft-item` behind a `MinecraftMCP_can-craft` check
- Call `MinecraftMCP_get-recipe` to identify missing materials
- Raw materials require `MinecraftMCP_smelt-item` before they can be used in recipes

**Block or entity not found**
- `MinecraftMCP_find-block` and `MinecraftMCP_find-entity` have limited range
- Use `MinecraftMCP_move-in-direction` to reposition and call find again

---

## 6. Best Practices

1. **Always call `MinecraftMCP_detect-gamemode` first** — it determines which movement tool to use
2. **Equip before acting** — call `MinecraftMCP_equip-item` before digging or combat
3. **Gate all crafting** — check `MinecraftMCP_can-craft` before attempting `MinecraftMCP_craft-item`
4. **Orient before interacting** — call `MinecraftMCP_look-at` before digging or placing for accuracy
5. **Verify after placing** — use `MinecraftMCP_get-block-info` to confirm block placement succeeded
6. **Log with chat** — use `MinecraftMCP_send-chat` for status updates and debugging
7. **Read chat for instructions** — call `MinecraftMCP_read-chat` to monitor server state or receive human input
8. **Smelt before crafting** — check `MinecraftMCP_list-inventory` for raw materials and smelt first
