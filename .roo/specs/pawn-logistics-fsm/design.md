# Design: Pawn-Mediated Vehicle Logistics FSM

**Spec ID:** `pawn-logistics-fsm`
**Created:** 2026-06-16
**Status:** approved
**Depends on:** `requirements.md`
**Architect Review:** Round 5 — reservation ownership map, HaulItem readonly record, driver state invariant

---

## 1. Architecture Overview

### 1.1 System Context

```
┌─────────────────────────────────────────────────────┐
│                  RimWorld Game Loop                  │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────┐  │
│  │  Map.Tick()  │→│ MapComponent │→│  Manager   │  │
│  └──────────────┘  │   .Tick()    │  │  .Tick()   │  │
│                    └──────────────┘  └─────┬─────┘  │
│                                            │         │
│  ┌─────────────────────────────────────────┼──────┐  │
│  │         AutoVehicleHaulManager          │      │  │
│  │                                         ▼      │  │
│  │  ┌──────────────────────────────────────────┐  │  │
│  │  │     Per-Vehicle FSM (VehicleJobContext)  │  │  │
│  │  │  IdleScan → MoveToPickup → Loading →     │  │  │
│  │  │  MoveToWarehouse → Unloading → IdleScan  │  │  │
│  │  │  ANY ──error──→ FailSafe → IdleScan      │  │  │
│  │  └──────────────────────────────────────────┘  │  │
│  │         │          │          │                 │  │
│  │         ▼          ▼          ▼                 │  │
│  │   ┌──────────┐ ┌────────┐ ┌──────────┐         │  │
│  │   │Vehicle   │ │ Pawn   │ │Inventory │         │  │
│  │   │Framework │ │ System │ │ (inner)  │         │  │
│  │   └──────────┘ └────────┘ └──────────┘         │  │
│  │                                                 │  │
│  │   ┌─────────────────────────────────────────┐   │  │
│  │   │  Reservation Authority (global)         │   │  │
│  │   │  Dictionary<Thing, VehicleJobContext>   │   │  │
│  │   │  reservedBy: Thing → owning context     │   │  │
│  │   └─────────────────────────────────────────┘   │  │
│  └─────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────┘
```

### 1.2 Component Responsibilities

| Component | Responsibility |
|-----------|---------------|
| `AutoVehicleHaulManager` | MapComponent host. Runs FSM tick. Manages `jobContexts` and `reservedBy`. |
| `VehicleJobContext` | Per-vehicle state: current state, driver ref, CargoPlan, sub-state, targets, timeouts. |
| `VehicleState` (enum) | 6 states: IdleScan, MoveToPickup, Loading, MoveToWarehouse, Unloading, FailSafe |
| `DriverPresence` (enum) | 3 states: None, OnVehicle, OnMap |
| `DriverRole` (enum) | 3 states: Idle, Driving, Working |
| `LoadingSubState` (enum) | 7 sub-states: Idle, Reserving, MovingToItem, PickingUp, StoringInVehicle, ItemDone, Failed |
| `CargoPlan` (readonly struct) | Immutable cargo intent: IReadOnlyList of HaulItems, pre-calculated mass/count |
| `HaulItem` (readonly record struct) | Single item: Thing, Position, Mass — completely immutable |
| `reservedBy` (Dictionary) | Global reservation ownership map: Thing → VehicleJobContext |
| FSM Tick | Evaluates current state → runs state logic → checks transitions → advances. |
| Driver Controller | Embarks/disembarks driver. Manages pawn draft state during haul phases. |
| Haul Planner | Scores and batches haulable items into CargoPlan. Resolves warehouse destinations. |
| Cargo Transfer | Moves items between world ↔ vehicle inventory ↔ stockpile. Transactional. |

---

## 2. State Machine Design

### 2.1 State Transitions

```
                     ┌──────────────────────────────────────┐
                     │                                      │
                     ▼                                      │
               ┌──────────┐    dispatch    ┌──────────────┐ │
               │ IdleScan │──────────────→│ MoveToPickup │ │
               └──────────┘                └──────┬───────┘ │
                     ▲                           │         │
                     │                           │ arrived │
                     │                           ▼         │
                     │                    ┌────────────┐   │
                     │                    │  Loading   │   │
                     │                    │ (sub-FSM)  │   │
                     │                    └──────┬─────┘   │
                     │                           │         │
                     │                           │ full/   │
                     │                           │ empty   │
                     │                           ▼         │
                     │                 ┌──────────────────┐│
                     │                 │ MoveToWarehouse  ││
                     │                 └────────┬─────────┘│
                     │                          │          │
                     │                          │ arrived  │
                     │                          ▼          │
                     │                  ┌────────────┐    │
                     │                  │ Unloading  │    │
                     │                  └──────┬─────┘    │
                     │                         │          │
                     │                         │ empty    │
                     │                         ▼          │
                     │                  ┌────────────┐    │
                     │                  │  FailSafe  │    │
                     │                  └──────┬─────┘    │
                     │                         │          │
                     └─────────────────────────┘          │
```

### 2.2 State Logic

#### IdleScan
```
TICK:
  1. If vehicle has active job context → skip (already dispatched)
  2. If FailSafe cooldown active → decrement, return
  3. Scan for haulable items in radius (reuse Phase 5 logic)
  4. Filter out items in reservedBy.Keys
  5. If no items → return
  6. Build CargoPlan ONCE (score, sort, capacity-bounded) — immutable readonly struct
  7. Resolve warehouse destination for CargoPlan
  8. Create VehicleJobContext
  9. Transition → MoveToPickup
```

#### MoveToPickup
```
TICK:
  1. If NOT at target (distance > 2 cells):
     a. If LastIssuedPathTarget != TargetPickupPos:
        - Call StartPath(TargetPickupPos)
        - Set LastIssuedPathTarget = TargetPickupPos
  2. If arrival predicate met (distance ≤ 2 cells from target):
     a. Anti-oscillation check: LastState != Loading OR TicksSinceLastTransition > 120
     b. Transition → Loading
```

#### Loading (with Sub-FSM)
```
ON ENTRY (first tick in state):
  1. DisembarkPawn(driver) → set DriverPresence = OnMap
  2. Build CargoPlan ONCE (scan 15-cell radius, filter designated, score by distance)
  3. Set CurrentItemIndex = 0, TransferredCount = 0
  4. Set SubState = Reserving

TICK:
  1. If !DriverExists → transition → FailSafe
  2. If DriverPresence == OnVehicle → DisembarkPawn, set DriverPresence = OnMap
  3. If CurrentItemIndex >= CargoPlan.Items.Count:
     a. Re-embark driver (TryAddPawn)
     b. Transition → MoveToWarehouse
  4. Run LoadingSubFSM(SubState, CurrentItemIndex)
  5. Assert IsDriverStateValid(ctx) every tick

LOADING SUB-FSM:
  Reserving:
    1. HaulItem item = CargoPlan.Items[CurrentItemIndex]
    2. If item.Item is null/destroyed → SubState = Failed
    3. If reservedBy.ContainsKey(item.Item) AND reservedBy[item.Item] != ctx:
       → SubState = Failed (another vehicle owns this reservation)
    4. reservedBy[item.Item] = ctx  // Atomic ownership claim
    5. SubState = MovingToItem

  MovingToItem:
    1. HaulItem item = CargoPlan.Items[CurrentItemIndex]
    2. If item.Item is null/destroyed → reservedBy.Remove(item.Item), SubState = Failed
    3. Move driver adjacent to item.Position (pawn pathing)
    4. If driver adjacent → SubState = PickingUp
    5. If SubStateTicks > 300 → reservedBy.Remove(item.Item), SubState = Failed (stuck)

  PickingUp:
    1. HaulItem item = CargoPlan.Items[CurrentItemIndex]
    2. If item.Item is null/destroyed → reservedBy.Remove(item.Item), SubState = Failed
    3. If NOT IsVehicleFull():
       a. item.Item.DeSpawn()
       b. bool added = vehicle.inventory.innerContainer.TryAdd(item.Item)
       c. If added → reservedBy.Remove(item.Item), TransferredCount++, SubState = ItemDone
       d. If NOT added → GenSpawn.Spawn(item.Item, item.Position, map), SubState = StoringInVehicle
    4. If IsVehicleFull() → reservedBy.Remove(item.Item), SubState = ItemDone (skip, vehicle full)
    5. If SubStateTicks > 120 → reservedBy.Remove(item.Item), SubState = Failed

  StoringInVehicle (fallback):
    1. Move driver adjacent to vehicle
    2. If driver adjacent AND NOT IsVehicleFull():
       a. item.Item.DeSpawn()
       b. vehicle.inventory.innerContainer.TryAdd(item.Item)
       c. reservedBy.Remove(item.Item)
       d. TransferredCount++
       e. SubState = ItemDone
    3. If SubStateTicks > 200 → reservedBy.Remove(item.Item), SubState = Failed

  ItemDone:
    1. CurrentItemIndex++
    2. If CurrentItemIndex >= CargoPlan.Items.Count:
       a. Re-embark driver
       b. Transition → MoveToWarehouse
    3. Else → SubState = Reserving (next item)

  Failed:
    1. CurrentItemIndex++
    2. If CurrentItemIndex >= CargoPlan.Items.Count:
       a. Re-embark driver
       b. Transition → MoveToWarehouse
    3. Else → SubState = Reserving (skip failed item, try next)

  Timeout: if TicksInState > 600 → re-embark, transition → MoveToWarehouse
```

#### MoveToWarehouse
```
TICK:
  1. If NOT at warehouse (distance > 2 cells):
     a. If LastIssuedPathTarget != TargetWarehousePos:
        - Call StartPath(TargetWarehousePos)
        - Set LastIssuedPathTarget = TargetWarehousePos
  2. If arrival predicate met:
     a. Anti-oscillation check
     b. Transition → Unloading
```

#### Unloading
```
ON ENTRY (first tick in state):
  1. DisembarkPawn(driver) → set DriverPresence = OnMap

TICK:
  1. If !DriverExists → transition → FailSafe
  2. If DriverPresence == OnVehicle → DisembarkPawn, set DriverPresence = OnMap
  3. If inventory.innerContainer.Count == 0:
     a. Re-embark driver
     b. Transition → IdleScan
     c. Remove jobContext
  4. If items remain:
     a. Pick next item from innerContainer
     b. If item is null/destroyed → remove from container, continue
     c. Move driver adjacent to vehicle
     d. Remove item from innerContainer
     e. GenSpawn.Spawn(item, stockpileCell, map)
  5. Timeout check: if TicksInState > 600 → force transition → FailSafe
```

#### FailSafe (Unified Failure Recovery)
```
ON ENTRY:
  1. Release ALL reservations: foreach (kvp in reservedBy where Value == ctx) → reservedBy.Remove(kvp.Key)
  2. If DriverPresence == OnMap:
     a. Re-embark driver (TryAddPawn)
  3. If vehicle != null AND vehicle.Spawned:
     a. vehicle.ignition.Drafted = false
  4. Remove jobContext from dictionary
  5. Set FailSafeCooldown = 250 ticks

TICK:
  1. Decrement FailSafeCooldown
  2. If FailSafeCooldown <= 0 → Transition → IdleScan
```

---

## 3. Driver Lifecycle

### 3.1 DriverPresence + DriverRole Enums

```csharp
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
```

**Existence check** (separate from presence/role):
```
bool DriverExists => DriverPawn != null && !DriverPawn.Dead && !DriverPawn.Destroyed;
```

**Invariant** (called every tick):
```
bool IsDriverStateValid(ctx):
    None → Idle
    OnVehicle → Driving or Idle
    OnMap → Working or Idle
```

### 3.2 Embark (Pawn → Vehicle)
```
1. Find best driver: FindBestDriver(vehicle, map)
2. vehicle.TryAddPawn(driver, movementHandler)
3. vehicle.ignition.Drafted = true
4. Set DriverPresence = OnVehicle, DriverRole = Driving
```

### 3.3 Disembark (Vehicle → Pawn)
```
1. vehicle.DisembarkPawn(driver)
2. driver.Drafted = true
3. Set DriverPresence = OnMap, DriverRole = Working
```

### 3.4 Re-embark (Pawn → Vehicle, after phase)
```
1. vehicle.TryAddPawn(driver, movementHandler)
2. Set DriverPresence = OnVehicle, DriverRole = Driving
```

### 3.5 Driver Draft State

| Phase | Driver Drafted | Why |
|-------|---------------|-----|
| Inside vehicle (driving) | No (vehicle is drafted) | Vehicle handles movement |
| Loading/Unloading | Yes | Pawn needs to perform haul jobs |
| Re-embarked (driving) | No | Vehicle handles movement again |

---

## 4. Cargo Transfer Strategy

### 4.1 Transactional Cargo Model

The CargoPlan + HaulItem system guarantees:

> "Either an item is fully committed to vehicle cargo OR it remains untouched in world state."

**CargoPlan is a `readonly struct`** — the Items list is set once via constructor as `IReadOnlyList<HaulItem>` and cannot be modified after creation.

**HaulItem is a `readonly record struct`** — completely immutable after construction. Transfer progress is tracked on the manager (via `reservedBy` map and `TransferredCount` on context), not on the HaulItem.

**Loading (World → Vehicle):**
```csharp
// Sub-state: PickingUp
item.Item.DeSpawn();  // Remove from world
bool added = vehicle.inventory.innerContainer.TryAdd(item.Item);
if (!added)
{
    // Restore to world — item is NOT lost
    GenSpawn.Spawn(item.Item, item.Position, map);
}
```

**Unloading (Vehicle → Stockpile):**
```csharp
Thing item = vehicle.inventory.innerContainer[i];
vehicle.inventory.innerContainer.Remove(item);
GenSpawn.Spawn(item, stockpileCell, map);
```

---

## 5. Warehouse Resolution

### 5.1 Algorithm
```
FindBestWarehouse(vehicle, cargoItems):
  1. Collect all Zone_Stockpile zones on map
  2. Filter zones that accept any cargoItems' ThingDef
  3. Filter zones with remaining stack capacity (not full)
  4. Filter zones owned by player faction
  5. Filter zones that are pathable (vehicle can reach at least one cell)
  6. Score by: distance to vehicle + zone remaining capacity + saturation penalty
  7. Return best zone's center cell
  8. Fallback: Find player home area center
```

### 5.2 Stockpile Query
```csharp
IEnumerable<Zone_Stockpile> stockpiles = map.zoneManager.ZoneListFor(ZoneType.Zone_Stockpile);
Zone_Stockpile best = stockpiles
    .Where(z => z.GetStoreSettings().AllowedToAccept(itemDef))
    .Where(z => z.Cells.Count > 0)
    .Where(z => IsPathable(vehicle, z.Cells[0]))
    .OrderBy(z => z.Cells[0].DistanceToSquared(vehicle.Position))
    .FirstOrDefault();
```

### 5.3 Saturation Prediction
```
IsZoneSaturated(Zone_Stockpile zone):
  int totalCells = zone.Cells.Count;
  int usedCells = zone.Cells.Count(c => c.GetThingList(map).Any(t => t.def == itemDef));
  float saturationRatio = (float)usedCells / totalCells;
  return saturationRatio > 0.8f;  // 80% full = saturated
```

---

## 6. Tick Strategy

### 6.1 Dual Tick Rates

| Mode | Interval | Purpose |
|------|----------|---------|
| IdleScan | 250 ticks | Full scan + dispatch decision (cheap) |
| Active FSM | 60 ticks | State machine tick when any vehicle has active job |

### 6.2 Implementation
```
MapComponentTick():
  // Always: cleanup finished jobs (cheap)
  CleanupFinishedJobs()

  // Every 250 ticks: scan + dispatch
  if (TicksGame % 250 == 0):
    FullScanAndDispatch()

  // Every 60 ticks: FSM tick (if any active jobs)
  if (TicksGame % 60 == 0 && jobContexts.Count > 0):
    foreach (ctx in jobContexts.Values):
      FSMTick(ctx)
```

---

## 7. Error Handling

| Error | Response |
|-------|----------|
| Driver pawn dies during job | Set DriverPresence = None, transition → FailSafe |
| Vehicle destroyed during job | Remove jobContext, transition → FailSafe |
| Item disappears (destroyed/stolen) | SubState = Failed, skip item, continue |
| Stockpile zone deleted mid-trip | Re-resolve warehouse, or fallback to home area |
| Pawn stuck (can't reach item) | SubState timeout → Failed → skip item |
| Vehicle can't path to target | Timeout after 600 ticks → FailSafe |
| No driver available | Skip dispatch, log warning |
| TryAdd fails (container full) | Restore item to world, SubState = StoringInVehicle fallback |
| Reservation conflict (another vehicle owns item) | SubState = Failed, skip item |

---

## 8. Anti-Oscillation Guards

```
Rule: After transitioning from state A to state B,
      the FSM MUST NOT re-enter state B within 120 ticks.

Implementation:
  - Track LastState in VehicleJobContext
  - Track TicksSinceLastTransition in VehicleJobContext
  - On each transition: LastState = previous, TicksSinceLastTransition = 0
  - On each tick: TicksSinceLastTransition++
  - Guard: if (nextState == LastState && TicksSinceLastTransition < 120) → block transition
```

---

## 9. Arrival Predicate

```
bool HasArrived(VehiclePawn vehicle, IntVec3 target, float threshold = 2.0f):
    return vehicle.Position.DistanceToSquared(target) <= (threshold * threshold)
```

---

## 10. File Structure

```
Source/AutoVehicleHaul/
├── AutoVehicleHaulManager.cs    // MapComponent host + FSM orchestrator + reservation authority
├── VehicleJobContext.cs         // Per-vehicle state (enums + classes)
├── CargoPlan.cs                 // Immutable readonly cargo plan + HaulItem record
├── DriverController.cs          // Embark/disembark/draft management
├── HaulPlanner.cs               // Item scanning, batching, scoring
├── CargoTransfer.cs             // Direct world↔vehicle↔stockpile transfer
└── WarehouseResolver.cs         // Stockpile zone query + selection
```

---

## 11. Implementation Guidance (Deferred to Vibe Mode)

The following are **not spec concerns** but are recorded here as guidance for the implementation phase:

| Concern | Guidance |
|---------|----------|
| Loading sub-FSM extraction | Extract each sub-state into its own method (`TickReserving`, `TickMovingToItem`, etc.) to avoid god-method |
| FSM consistency check | Add `bool IsConsistent(VehicleJobContext ctx)` — run every tick in debug builds to detect VF/FSM state divergence |
| Driver state invariant | Call `Debug.Assert(IsDriverStateValid(ctx))` every FSM tick |
| Reservation ownership | When reserving, check `reservedBy.TryGetValue(item, out var owner)` — if owner != ctx, the claim fails |
| HaulItem immutability | Use `readonly record struct HaulItem(Thing Item, IntVec3 Position, float Mass)` — all fields set via constructor only |
| FailSafe orchestration | FailSafe should call `ReleaseReservations(ctx)`, `ReembarkDriver(ctx)`, `ResetVehicle(ctx)`, `RemoveJobContext(ctx)` — not implement cleanup inline |
| Manager decomposition | If AutoVehicleHaulManager exceeds ~500 lines, extract LoadingSystem, WarehouseSystem, DispatchSystem |
| Path invalidation | If map changes (new buildings), clear `LastIssuedPathTarget` to force re-path |
