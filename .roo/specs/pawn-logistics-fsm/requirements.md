# Requirements: Pawn-Mediated Vehicle Logistics FSM

**Spec ID:** `pawn-logistics-fsm`
**Created:** 2026-06-16
**Status:** approved
**Depends on:** Phase 6 vehicle dispatch (commit `655e240`)
**Architect Review:** Round 5 — reservation ownership map, HaulItem readonly record, driver state invariant

---

## 1. Problem Statement

Current system (Phase 6) proves a vehicle can be commanded to **move** to a target tile. But movement alone does not constitute hauling. The vehicle arrives at a designated item and then... nothing happens. The item sits on the ground.

**Root cause:** RimWorld's architecture separates **movement** (pawn/vehicle pather) from **interaction** (pawn job system). VehicleFramework vehicles are transport shells — they move but do not interact. Only pawns interact. The pawn must exit the vehicle, perform haul jobs, and (optionally) return.

**Goal:** Implement a finite-state machine (FSM) that orchestrates the full logistics cycle:

```
IdleScan → MoveToPickup → Loading → MoveToWarehouse → Unloading → IdleScan
```

Where the **Loading** phase has the driver pawn physically haul nearby designated items into the vehicle inventory, and the **Unloading** phase deposits cargo at the nearest appropriate stockpile.

---

## 2. Current System State (What Works)

| Component | Status | Location |
|-----------|--------|----------|
| Vehicle scanning (idle detection) | ✅ Working | `AutoVehicleHaulManager.MapComponentTick` |
| Haul designation detection | ✅ Working | Phase 5 scan loop |
| Candidate scoring + ranking | ✅ Working | Phase 4 scoring |
| Driver assignment via `TryAddPawn` | ✅ Working | Phase 6b |
| Vehicle drafting (normal API) | ✅ Working | Phase 6c |
| Vehicle movement via `StartPath` | ✅ Working | Phase 6d |
| Driver cleanup on arrival | ✅ Working | `assignedDrivers` dictionary |

### Key APIs Confirmed from VehicleFramework Source

| API | Purpose | Source File |
|-----|---------|-------------|
| `VehiclePawn.TryAddPawn(Pawn, VehicleRoleHandler)` | Assign pawn to role slot | `VehiclePawn_Handlers.cs:401` |
| `VehiclePawn.DisembarkPawn(Pawn)` | Remove pawn from vehicle, spawn near it | `VehiclePawn_Handlers.cs:485` |
| `VehiclePawn.ignition.Drafted` | Draft/undraft (gated by `CanDraft()`) | `VehicleIgnitionController.cs:22` |
| `VehiclePawn.vehiclePather.StartPath()` | Begin pathing to target | `VehiclePathFollower.cs:271` |
| `VehiclePawn.inventory.innerContainer` | Cargo storage (`ThingOwner<Thing>`) | `VehiclePawn.cs:144` |
| `VehiclePawn.CanMoveFinal` | `CanMove && HasEnoughOperators` | `VehiclePawn_Health.cs:34` |
| `VehiclePawn.HasEnoughOperators` | All movement roles fulfilled | `VehiclePawn_Handlers.cs:72` |
| `VehicleRoleHandler.CanOperateRole(Pawn)` | Faction/capability check | `VehicleRoleHandler.cs:125` |
| `VehicleRoleHandler.RoleFulfilled` | Slot occupancy check | `VehicleRoleHandler.cs:73` |
| `Pawn.InVehicle()` | Check if pawn is in any vehicle | RimWorld `Pawn` |
| `Pawn.DeSpawn()` | Remove pawn from world map | RimWorld `Pawn` |
| `GenSpawn.Spawn(pawn, loc, map)` | Place pawn on map | RimWorld |

---

## 3. Desired Behavior

### 3.1 Full Lifecycle

```
┌─────────────┐
│  IdleScan   │  Find haulable items, score, pick best cluster
└──────┬──────┘
       │ dispatch (CargoPlan created BEFORE movement)
       ▼
┌─────────────┐
│MoveToPickup │  Drive vehicle to best item/cluster position
└──────┬──────┘
       │ vehicle arrives (arrival predicate: distance ≤ 2 cells)
       ▼
┌─────────────┐
│   Loading   │  Driver exits, hauls items into vehicle inventory
│  (sub-FSM)  │  Reserving → MovingToItem → PickingUp → Storing
└──────┬──────┘
       │ vehicle full OR no more items in CargoPlan
       ▼
┌─────────────┐
│MoveToWarehouse│  Drive to nearest stockpile/warehouse zone
└──────┬──────┘
       │ vehicle arrives
       ▼
┌─────────────┐
│  Unloading  │  Driver moves items to stockpile
└──────┬──────┘
       │ inventory empty
       ▼
┌─────────────┐
│  IdleScan   │  Return to scanning
└─────────────┘

ANY STATE ──error──→ ┌──────────────┐
                     │  FailSafe    │  Release reservations, cleanup, cooldown
                     └──────┬───────┘
                            │
                            ▼
                     ┌──────────────┐
                     │  IdleScan    │  (after cooldown)
                     └──────────────┘
```

### 3.2 Loading Phase Detail (with Sub-FSM)

When vehicle arrives at pickup location, Loading operates as a **micro-FSM** with sub-states:

```
LoadingSubState:
   Reserving      → Reserve next item (prevent other pawns from grabbing it)
   MovingToItem   → Driver walks to item location
   PickingUp      → Driver picks up item into pawn inventory
   StoringInVehicle → Driver walks to vehicle, deposits into innerContainer
```

Each sub-state transitions on completion or failure. If any sub-state fails (item gone, path blocked, capacity full), the item is skipped and the next item begins.

**Loading Phase Detail:**

1. **Disembark driver** — `vehicle.DisembarkPawn(driver)` spawns pawn on adjacent standable cell
2. **Build CargoPlan ONCE** — scan radius for items with `DesignationOf.Haul`, filter by reachability, score by distance/mass. **Immutable for the duration of the Loading phase.**
3. **Sub-FSM loop** — for each item in CargoPlan:
   - `Reserving`: Reserve item in global `reservedBy` map (atomic ownership)
   - `MovingToItem`: Driver paths to item
   - `PickingUp`: Direct transfer attempt (DeSpawn + innerContainer.TryAdd)
   - `StoringInVehicle`: If direct transfer fails, try pawn-carry path
   - On failure at any sub-state: skip item, release reservation, continue
4. **Re-embark driver** — `vehicle.TryAddPawn(driver, movementHandler)` returns pawn to vehicle
5. **Transition** to `MoveToWarehouse`

### 3.3 Unloading Phase Detail

When vehicle arrives at warehouse/stockpile:

1. **Disembark driver** (same as loading)
2. **Unload loop** — for each item in `vehicle.inventory.innerContainer`:
   - Direct transfer: remove from innerContainer, spawn at stockpile cell
   - If transfer fails: skip item, continue
3. **Re-embark driver**
4. **Transition** to `IdleScan`

### 3.4 Warehouse Selection

- Find nearest `Zone_Stockpile` or `Building_Storage` that accepts the cargo type
- **Filter by:** stack limits (zone not full), reserved slots, player faction ownership, **pathability** (vehicle can reach drop zone), **saturation prediction** (zone not near full)
- Fallback: nearest player home area cell if no stockpile found
- Resolved per-trip at dispatch time (not per-vehicle)

### 3.5 CargoPlan (Transactional Cargo Model)

The CargoPlan is an **immutable intent object** created once per job. It guarantees:

> "Either an item is fully committed to vehicle cargo OR it remains untouched in world state."

```csharp
public readonly struct CargoPlan
{
    public IReadOnlyList<HaulItem> Items { get; }  // Immutable after creation
    public VehiclePawn TargetVehicle { get; }
    public IntVec3 PickupPos { get; }
    public IntVec3 WarehousePos { get; }
    public int TotalMass { get; }                    // Pre-calculated
    public int TotalCount { get; }                   // Pre-calculated
}

public readonly record struct HaulItem(
    Thing Item,
    IntVec3 Position,
    float Mass
);
```

**Invariant:** An item is NEVER in a half-transferred state. If `TryAdd` fails, the item remains in the world at its original position.

**Immutability:** CargoPlan is a `readonly struct`. HaulItem is a `readonly record struct`. Neither can be mutated after creation. Transfer progress is tracked on the manager (via `reservedBy` map and `TransferredCount` on context), not on the HaulItem itself.

### 3.6 Reservation Authority

The manager maintains a **global reservation map** to prevent cross-vehicle double-claiming:

```csharp
private static readonly Dictionary<Thing, VehicleJobContext> reservedBy = new();
```

- When Loading reserves an item: `reservedBy[item] = ctx` (atomic ownership — if another vehicle already reserved it, the claim fails)
- When transfer succeeds: `reservedBy.Remove(item)`
- When transfer fails: `reservedBy.Remove(item)`
- When scanning for haulable items: filter out anything where `reservedBy.ContainsKey(item)`
- On FailSafe: clear all reservations where `reservedBy[item] == ctx`

**Rationale:** A `HashSet<Thing>` only tracks IF something is reserved, not WHO owns it. The `Dictionary<Thing, VehicleJobContext>` map enables ownership checks — if vehicle A tries to reserve an item that vehicle B already reserved, the claim is rejected. This eliminates the "two vehicles reserve same item in same tick window" race condition.

### 3.7 Driver State Invariant

The DriverPresence and DriverRole enums have a **valid combination matrix**:

| DriverPresence | Valid DriverRoles |
|----------------|-------------------|
| None           | Idle              |
| OnVehicle      | Driving, Idle     |
| OnMap          | Working, Idle     |

**Invariant check:**
```csharp
public static bool IsDriverStateValid(VehicleJobContext ctx)
{
    if (ctx.DriverPresence == DriverPresence.None)
        return ctx.DriverRole == DriverRole.Idle;
    if (ctx.DriverPresence == DriverPresence.OnVehicle)
        return ctx.DriverRole == DriverRole.Driving || ctx.DriverRole == DriverRole.Idle;
    if (ctx.DriverPresence == DriverPresence.OnMap)
        return ctx.DriverRole == DriverRole.Working || ctx.DriverRole == DriverRole.Idle;
    return false;
}
```

This is called every FSM tick (debug + release assert) to detect state desync early.

---

## 4. Architect Decisions (Locked)

### 4.1 FSM Owns Orchestration, Pawn Job System Executes Micro-Actions

**Decision:** FSM decides *when* and *what*. Pawn JobDriver executes *how*. Do NOT replace JobDriver system.

### 4.2 Vehicle Inventory = Canonical Storage

**Decision:** Cargo goes into `vehicle.inventory.innerContainer` ONLY. Pawn inventory is transporter-only (temporary carry during haul). Never store cargo in pawn inventory permanently.

### 4.3 Pawn Exits Vehicle ONLY During Load/Unload Phases

**Decision:** Pawn must NEVER exit for scanning, driving, or pathing. Only exit when:
- `State == Loading`
- `State == Unloading`

### 4.4 Spatial + Capacity Bounded Greedy Batching

**Decision:** CargoPlan is created BEFORE movement starts (not mid-drive). CargoPlan is **immutable** once created — do not add/remove items during Loading.

### 4.5 StartPath Called ONLY on Destination Change

**Decision:** `StartPath()` must ONLY be called when the target position differs from the last issued path target. Use deterministic comparison, not a boolean flag:

```
if (ctx.LastIssuedPathTarget != ctx.TargetPickupPos):
    StartPath(ctx.TargetPickupPos)
    ctx.LastIssuedPathTarget = ctx.TargetPickupPos
```

### 4.6 Direct Transfer for v1 (Job-Mediated for v2)

**Decision:** For v1 prototype, direct `DeSpawn()` + `innerContainer.TryAdd()` is acceptable when driver is adjacent to item. v2 should migrate to proper `JobMaker.MakeJob(JobDefOf.HaulToCell, ...)` with a custom `Toil`.

### 4.7 Arrival Predicate (Not Position Equality)

**Decision:** Do NOT use exact position equality to detect arrival. Use distance-based arrival predicate:
```
arrived = vehicle.Position.DistanceToSquared(targetPos) <= arrivalThresholdSquared
```
Where `arrivalThresholdSquared = 4` (2 cells squared) for pickup, configurable per state.

### 4.8 Driver State Tracking (Presence + Role Split)

**Decision:** Split driver tracking into two enums — **presence** (where is the pawn?) and **role** (what is the pawn doing?):

```
enum DriverPresence
{
    None,        // No driver assigned
    OnVehicle,   // Pawn is in vehicle role slot (not on map)
    OnMap        // Pawn is on map (disembarked)
}

enum DriverRole
{
    Idle,        // Not performing any action
    Driving,     // Vehicle is drafted, pawn is moving vehicle
    Working      // Pawn is performing haul actions on map
}
```

Combined with existence check:
```
bool DriverExists => DriverPawn != null && !DriverPawn.Dead && !DriverPawn.Destroyed;
```

### 4.9 Transactional Cargo Model (CargoPlan)

**Decision:** A CargoPlan object is created once per job. It is a `readonly struct` with `IReadOnlyList<HaulItem>`. HaulItem is a `readonly record struct` to prevent any mutation after creation.

### 4.10 Loading Sub-FSM (Micro-States)

**Decision:** Loading operates as a micro-FSM with explicit sub-states:
```
Reserving → MovingToItem → PickingUp → StoringInVehicle → (next item)
```

### 4.11 FailSafeState (Unified Failure Recovery)

**Decision:** ANY state can transition to `FailSafe` on error. FailSafe is a single unified escape route:
```
ANY STATE → (error) → FailSafe → ReleaseReservations → Cleanup → Cooldown → IdleScan
```

FailSafe orchestrates cleanup by calling:
1. `ReleaseReservations(ctx)` — removes all entries from `reservedBy` where value == ctx
2. `ReembarkDriver(ctx)` — returns driver to vehicle if on map
3. `ResetVehicle(ctx)` — undrafts vehicle
4. `RemoveJobContext(ctx)` — removes from jobContexts dictionary

### 4.12 Reservation Authority (Global Ownership Map)

**Decision:** The manager maintains a global `Dictionary<Thing, VehicleJobContext> reservedBy` as the single source of truth for item reservations. The key is the Thing, the value is the owning context. This enables ownership checks and prevents race conditions.

### 4.13 Driver State Invariant Enforcement

**Decision:** The `IsDriverStateValid(ctx)` function is called every FSM tick to detect desync between presence and role. This is a debug assertion in development builds and a logged warning in release builds.

---

## 5. Constraints & Invariants

### 5.1 Framework Invariants (MUST satisfy)

| Invariant | How |
|-----------|-----|
| `vehicle.Drafted == true` before `StartPath` | Set `ignition.Drafted = true` after driver assigned |
| `HasEnoughOperators == true` for `CanMoveFinal` | Driver must be in movement handler before drafting |
| `Pawn.Drafted` for haul jobs | Driver must be drafted while performing haul jobs |
| `Pawn.Spawned` for job execution | Pawn must be on map (not inside vehicle) to run jobs |
| `vehicle.inventory.innerContainer` capacity | Check before each load; don't overfill |

### 5.2 Cargo Invariants (MUST satisfy)

| Invariant | How |
|-----------|-----|
| Item is NEVER half-transferred | DeSpawn only after TryAdd succeeds; if TryAdd fails, item stays in world |
| Reservation prevents double-haul | Global `reservedBy` map with ownership; cross-vehicle safe |
| CargoPlan is immutable | `readonly struct` with `IReadOnlyList<HaulItem>`; built once on Loading entry |
| HaulItem is immutable | `readonly record struct`; no mutation after creation |
| All reservations released on job end | FailSafe + normal completion both release all reservations |
| Driver state is consistent | `IsDriverStateValid(ctx)` called every tick |

### 5.3 Safety Guards

| Guard | Threshold |
|-------|-----------|
| Max haul items per loading tick | 5 (prevent infinite loop) |
| Max unload items per unloading tick | 5 |
| Job timeout (ticks per item) | 600 ticks (~10 seconds) |
| Max search radius for haulables | 15 cells from vehicle |
| Max search radius for warehouses | 50 cells from vehicle |
| Stuck detection: if driver hasn't moved in 300 ticks | Cancel job, re-embark, skip item |
| FailSafe cooldown | 250 ticks before re-dispatch after failure |

### 5.4 State Transition Guards

| Transition | Guard |
|------------|-------|
| IdleScan → MoveToPickup | Has haulable targets AND idle vehicle available |
| MoveToPickup → Loading | Arrival predicate met AND driver presence is `OnVehicle` |
| Loading → MoveToWarehouse | Vehicle full OR CargoPlan exhausted OR timeout |
| MoveToWarehouse → Unloading | Arrival predicate met AND driver presence is `OnVehicle` |
| Unloading → IdleScan | Inventory empty OR timeout |
| ANY → FailSafe | Error detected (driver missing, vehicle destroyed, item gone, stuck) |

**Anti-oscillation rule:** After transitioning from any state, the FSM MUST NOT re-enter the same state within 120 ticks. Track `LastState` and `TicksSinceLastTransition` in context.

---

## 6. Data Model

### 6.1 VehicleJobContext (per-vehicle state)

```csharp
public enum VehicleState
{
    IdleScan,
    MoveToPickup,
    Loading,
    MoveToWarehouse,
    Unloading,
    FailSafe     // Unified failure recovery state
}

public enum DriverPresence
{
    None,        // No driver assigned
    OnVehicle,   // Pawn is in vehicle role slot (not on map)
    OnMap        // Pawn is on map (disembarked)
}

public enum DriverRole
{
    Idle,        // Not performing any action
    Driving,     // Vehicle is drafted, pawn is moving vehicle
    Working      // Pawn is performing haul actions on map
}

public enum LoadingSubState
{
    Idle,              // Not processing any item
    Reserving,         // Reserving next item
    MovingToItem,      // Driver walking to item
    PickingUp,         // Transferring item to vehicle
    StoringInVehicle,  // Depositing into innerContainer
    ItemDone,          // Current item complete, advance to next
    Failed             // Current item failed, skip to next
}

public class VehicleJobContext
{
    public VehicleState State;
    public VehicleState LastState;         // Anti-oscillation tracking
    public int TicksSinceLastTransition;   // Anti-oscillation counter
    public VehiclePawn Vehicle;
    public Pawn DriverPawn;
    public DriverPresence DriverPresence;  // Where the driver is
    public DriverRole DriverRole;          // What the driver is doing
    public IntVec3 TargetPickupPos;
    public IntVec3 TargetWarehousePos;
    public IntVec3 LastIssuedPathTarget;   // Deterministic path target tracking
    public CargoPlan Plan;                 // Immutable cargo plan
    public LoadingSubState SubState;       // Current Loading sub-state
    public int SubStateTicks;              // Ticks in current sub-state
    public int CurrentItemIndex;           // Index in CargoPlan.Items
    public int TransferredCount;           // Number of items successfully transferred
    public int TicksInState;
    public int CurrentJobTimeout;
    public int FailSafeCooldown;           // Ticks before re-dispatch after failure
}

public readonly struct CargoPlan
{
    public IReadOnlyList<HaulItem> Items { get; }
    public VehiclePawn TargetVehicle { get; }
    public IntVec3 PickupPos { get; }
    public IntVec3 WarehousePos { get; }
    public int TotalMass { get; }
    public int TotalCount { get; }

    public CargoPlan(List<HaulItem> items, VehiclePawn vehicle, IntVec3 pickup, IntVec3 warehouse)
    {
        Items = items.AsReadOnly();
        TargetVehicle = vehicle;
        PickupPos = pickup;
        WarehousePos = warehouse;
        TotalCount = items.Count;
        TotalMass = items.Sum(i => i.Mass);
    }
}

public readonly record struct HaulItem(Thing Item, IntVec3 Position, float Mass);
```

### 6.2 Manager State Extension

Replace `Dictionary<VehiclePawn, Pawn> assignedDrivers` with:
```csharp
private static readonly Dictionary<VehiclePawn, VehicleJobContext> jobContexts = new();
private static readonly Dictionary<Thing, VehicleJobContext> reservedBy = new();  // Global reservation ownership
```

---

## 7. Implementation Phases

### Phase 7a: FSM Skeleton + Loading Phase

**Scope:**
- Add `VehicleState` enum (including `FailSafe`), `DriverPresence` enum, `DriverRole` enum, `LoadingSubState` enum
- Add `CargoPlan` (readonly struct) and `HaulItem` (readonly record struct)
- Add `VehicleJobContext` class with SubState tracking
- Add global `reservedBy` Dictionary on manager
- Refactor `assignedDrivers` → `jobContexts`
- Implement state machine tick in `MapComponentTick`
- Implement `Loading` state with sub-FSM:
  - Reserving → MovingToItem → PickingUp → StoringInVehicle
  - Build CargoPlan ONCE on state entry (immutable)
  - For each HaulItem: sub-FSM loop with reservation semantics
  - Re-embark driver when done
- Implement `FailSafe` state:
  - Release all reservations
  - Re-embark driver
  - Remove jobContext
  - Cooldown timer
- Implement `IsVehicleFull` check (count items in `innerContainer`)
- Implement `HasNearbyHaulables` check
- **StartPath only on destination change** (deterministic: `LastIssuedPathTarget != target`)
- **Arrival predicate** (distance-based, not position equality)
- **Anti-oscillation guards** (LastState + TicksSinceLastTransition)
- **Driver state invariant** (`IsDriverStateValid` every tick)

**Success criteria:**
- Vehicle drives to item zone
- Driver exits vehicle
- Driver picks up at least one item and puts it in vehicle inventory
- Driver re-enters vehicle
- Log shows state transitions including sub-states

### Phase 7b: Warehouse Selection + Return Trip

**Scope:**
- Implement `FindBestWarehouse(vehicle, cargoType)` — scan for `Zone_Stockpile` zones
  - Filter by: stack limits, reserved slots, faction ownership, **pathability**, **saturation prediction**
- Implement `MoveToWarehouse` state: drive to warehouse position
- Implement `Unloading` state:
  - Disembark driver
  - For each item in `innerContainer`: direct transfer to stockpile
  - Re-embark when done
- Implement full cycle: IdleScan → ... → Unloading → IdleScan

**Success criteria:**
- Vehicle returns to stockpile zone after loading
- Items appear in stockpile after unloading
- Vehicle returns to idle scanning

### Phase 7c: Polish + Edge Cases

**Scope:**
- Timeout guards (stuck detection, job cancellation)
- Capacity-aware haul list (stop when full mid-loop)
- Multiple item types in single trip
- Driver death/injury handling
- Vehicle destruction handling
- Undrivered vehicle recovery
- FailSafe edge cases (double failure, cascading errors)

---

## 8. Out of Scope (for this spec)

- Multi-vehicle fleet coordination
- Fuel management / refueling
- Loading speed / carrying capacity upgrades
- UI for vehicle job status
- Save/load persistence of FSM state (can rebuild from world state on load)
- Unload-to-specific-stockpile-priority logic (just use nearest accepting stockpile)
- JobDriver-based hauling (deferred to v2)
