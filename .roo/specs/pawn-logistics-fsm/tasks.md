# Tasks: Pawn-Mediated Vehicle Logistics FSM

**Spec ID:** `pawn-logistics-fsm`
**Created:** 2026-06-16
**Status:** approved
**Depends on:** `design.md`
**Architect Review:** Round 5 — reservation ownership map, HaulItem readonly record, driver state invariant

---

## Phase 7a: FSM Skeleton + Loading Phase

### Task 7a-1: Create VehicleJobContext state model

**File:** `Source/AutoVehicleHaul/VehicleJobContext.cs` (new)

- [ ] Define `VehicleState` enum: `IdleScan`, `MoveToPickup`, `Loading`, `MoveToWarehouse`, `Unloading`, `FailSafe`
- [ ] Define `DriverPresence` enum: `None`, `OnVehicle`, `OnMap`
- [ ] Define `DriverRole` enum: `Idle`, `Driving`, `Working`
- [ ] Define `LoadingSubState` enum: `Idle`, `Reserving`, `MovingToItem`, `PickingUp`, `StoringInVehicle`, `ItemDone`, `Failed`
- [ ] Define `HaulItem` as `readonly record struct HaulItem(Thing Item, IntVec3 Position, float Mass)`
- [ ] Define `CargoPlan` readonly struct: `Items` (IReadOnlyList<HaulItem>), `TargetVehicle`, `PickupPos`, `WarehousePos`, `TotalMass`, `TotalCount`
  - [ ] Constructor takes `List<HaulItem>`, converts to `AsReadOnly()`
  - [ ] All fields are get-only (immutable)
- [ ] Define `VehicleJobContext` class with fields:
  - `State`, `LastState` (VehicleState), `TicksSinceLastTransition` (int)
  - `Vehicle` (VehiclePawn), `DriverPawn` (Pawn)
  - `DriverPresence` (DriverPresence), `DriverRole` (DriverRole)
  - `TargetPickupPos` (IntVec3), `TargetWarehousePos` (IntVec3)
  - `LastIssuedPathTarget` (IntVec3) — deterministic path tracking
  - `Plan` (CargoPlan)
  - `SubState` (LoadingSubState), `SubStateTicks` (int), `CurrentItemIndex` (int)
  - `TransferredCount` (int) — track successful transfers
  - `TicksInState` (int), `CurrentJobTimeout` (int)
  - `FailSafeCooldown` (int)
- [ ] Add `using Vehicles;`, `using Verse;`, `using System.Collections.Generic;`, `using System.Linq;`

### Task 7a-2: Refactor AutoVehicleHaulManager — replace assignedDrivers with jobContexts

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] Replace `Dictionary<VehiclePawn, Pawn> assignedDrivers` with `Dictionary<VehiclePawn, VehicleJobContext> jobContexts`
- [ ] Add `Dictionary<Thing, VehicleJobContext> reservedBy` as global reservation ownership map
- [ ] Remove `using System.Reflection;` (no longer needed)
- [ ] Remove `IgnitionDraftedField` static field
- [ ] Add `using System.Collections.Generic;` (if not present)

### Task 7a-3: Implement dual tick rate

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] Keep existing 250-tick scan for `IdleScan` state logic
- [ ] Add 60-tick FSM tick block: if `jobContexts.Count > 0`, iterate and call `FSMTick(ctx)` for each
- [ ] FSM tick block runs AFTER the 250-tick scan (so dispatch and FSM can coexist)

### Task 7a-4: Implement FSMTick dispatcher

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] Add method `void FSMTick(VehicleJobContext ctx)`
- [ ] Switch on `ctx.State`:
  - `IdleScan` → `TickIdleScan(ctx)`
  - `MoveToPickup` → `TickMoveToPickup(ctx)`
  - `Loading` → `TickLoading(ctx)`
  - `MoveToWarehouse` → `TickMoveToWarehouse(ctx)`
  - `Unloading` → `TickUnloading(ctx)`
  - `FailSafe` → `TickFailSafe(ctx)`
- [ ] Increment `ctx.TicksInState` each tick
- [ ] Increment `ctx.TicksSinceLastTransition` each tick
- [ ] Add timeout check: if `TicksInState > max`, force transition to next state
- [ ] Add anti-oscillation check before each transition
- [ ] Call `Debug.Assert(IsDriverStateValid(ctx))` every tick

### Task 7a-5: Implement IdleScan → MoveToPickup transition

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] Extract existing scan logic (Phase 5) into `TickIdleScan(ctx)` method
- [ ] After scoring, if candidates exist:
  - [ ] Build CargoPlan (scan, filter, score, capacity-bounded) — readonly struct
  - [ ] Resolve warehouse destination for CargoPlan
  - [ ] Create `VehicleJobContext` with `State = MoveToPickup`
  - [ ] Set `TargetPickupPos` to best candidate position
  - [ ] Assign driver via existing `FindBestDriver` + `TryAddPawn` logic
  - [ ] Set `DriverPresence = OnVehicle`, `DriverRole = Driving`
  - [ ] Draft vehicle via `ignition.Drafted = true`
  - [ ] Set `LastIssuedPathTarget = IntVec3.Zero` (forces StartPath on first tick)
  - [ ] Add to `jobContexts`
- [ ] Guard: only dispatch if vehicle not already in `jobContexts`
- [ ] Guard: skip if `FailSafeCooldown > 0`

### Task 7a-6: Implement MoveToPickup state

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] `TickMoveToPickup(ctx)`:
  - [ ] Use arrival predicate: `vehicle.Position.DistanceToSquared(target) <= 4` (2 cells squared)
  - [ ] If NOT arrived AND `ctx.LastIssuedPathTarget != ctx.TargetPickupPos`:
    - [ ] Call `StartPath(ctx.TargetPickupPos)`
    - [ ] Set `ctx.LastIssuedPathTarget = ctx.TargetPickupPos`
  - [ ] If arrival predicate met:
    - [ ] Anti-oscillation check: `ctx.LastState != Loading || ctx.TicksSinceLastTransition > 120`
    - [ ] Transition to `Loading`

### Task 7a-7: Implement Loading state with Sub-FSM

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] `TickLoading(ctx)`:
  - [ ] **On entry** (first tick in state, `TicksInState == 0`):
    - [ ] If `ctx.DriverPresence == OnVehicle`: call `ctx.Vehicle.DisembarkPawn(ctx.DriverPawn)`, set `ctx.DriverPresence = OnMap`, `ctx.DriverRole = Working`
    - [ ] Build CargoPlan ONCE (scan 15-cell radius for designated haul items, score by distance)
    - [ ] Set `ctx.CurrentItemIndex = 0`, `ctx.TransferredCount = 0`
    - [ ] Set `ctx.SubState = LoadingSubState.Reserving`
  - [ ] If `!DriverExists(ctx)`: transition to `FailSafe`
  - [ ] If `ctx.CurrentItemIndex >= ctx.Plan.Items.Count`:
    - [ ] Re-embark driver (`TryAddPawn`)
    - [ ] Transition to `MoveToWarehouse`
  - [ ] Run LoadingSubFSM:
    - [ ] **Reserving**: `reservedBy[item.Item] = ctx` (check for conflict first). If item null/destroyed or conflict → `Failed`
    - [ ] **MovingToItem**: Move driver adjacent to item. If adjacent → `PickingUp`. If stuck > 300 ticks → `Failed`
    - [ ] **PickingUp**: If NOT full: `item.DeSpawn()` → `TryAdd()`. If success → `reservedBy.Remove()`, `TransferredCount++`, `ItemDone`. If fail → `StoringInVehicle`. If full → `ItemDone`
    - [ ] **StoringInVehicle**: Move driver to vehicle, attempt `TryAdd()`. If success → `reservedBy.Remove()`, `TransferredCount++`, `ItemDone`. If timeout → `Failed`
    - [ ] **ItemDone**: `CurrentItemIndex++`. If done → re-embark, `MoveToWarehouse`. Else → `Reserving`
    - [ ] **Failed**: `reservedBy.Remove(item.Item)`, `CurrentItemIndex++`. If done → re-embark, `MoveToWarehouse`. Else → `Reserving`
  - [ ] Timeout guard (max 600 ticks in Loading)

### Task 7a-8: Implement FailSafe state

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] `TickFailSafe(ctx)`:
  - [ ] **On entry** (first tick in state):
    - [ ] Release reservations: foreach (kvp in `reservedBy` where `Value == ctx`) → `reservedBy.Remove(kvp.Key)`
    - [ ] If `ctx.DriverPresence == OnMap`: re-embark driver (`TryAddPawn`)
    - [ ] If vehicle != null AND vehicle.Spawned: `vehicle.ignition.Drafted = false`
    - [ ] Remove jobContext from dictionary
    - [ ] Set `ctx.FailSafeCooldown = 250`
  - [ ] Decrement `ctx.FailSafeCooldown` each tick
  - [ ] If `FailSafeCooldown <= 0`: transition to `IdleScan`

### Task 7a-9: Implement helper methods

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] `bool IsVehicleFull(VehiclePawn vehicle)` — check `inventory.innerContainer.Count` against a threshold
- [ ] `bool HasNearbyHaulables(VehiclePawn vehicle, int radius)` — scan for designated haul items, filter out `reservedBy.Keys`
- [ ] `CargoPlan BuildCargoPlan(VehiclePawn vehicle, int radius)` — scan, filter, score, return readonly struct
- [ ] `bool HasArrived(VehiclePawn vehicle, IntVec3 target, float threshold)` — distance-based arrival predicate
- [ ] `bool DriverExists(VehicleJobContext ctx)` — `ctx.DriverPawn != null && !ctx.DriverPawn.Dead && !ctx.DriverPawn.Destroyed`
- [ ] `bool IsDriverStateValid(VehicleJobContext ctx)` — Presence↔Role consistency check
- [ ] `void DisembarkDriver(VehicleJobContext ctx)` — wrapper around `DisembarkPawn` + draft + set `DriverPresence = OnMap`
- [ ] `void ReembarkDriver(VehicleJobContext ctx)` — wrapper around `TryAddPawn` + set `DriverPresence = OnVehicle`
- [ ] `void CleanupFinishedJobs()` — remove destroyed vehicles, completed jobs from `jobContexts`
- [ ] `void TransitionState(VehicleJobContext ctx, VehicleState newState)` — update `LastState`, reset `TicksInState`, set `TicksSinceLastTransition = 0`
- [ ] `void ReleaseReservations(VehicleJobContext ctx)` — remove all entries from `reservedBy` where value == ctx

### Task 7a-10: Build and test Phase 7a

- [ ] `dotnet build -c Debug` — zero errors
- [ ] Test in RimWorld: vehicle should drive to item zone, driver should exit, items should appear in vehicle inventory
- [ ] Verify log shows state transitions: IdleScan → MoveToPickup → Loading (with sub-states)
- [ ] Verify FailSafe triggers on driver death or vehicle destruction
- [ ] Verify `reservedBy` prevents double-claiming when two vehicles are dispatched
- [ ] Verify `IsDriverStateValid` does not fire false positives during normal operation

---

## Phase 7b: Warehouse Selection + Return Trip

### Task 7b-1: Implement WarehouseResolver

**File:** `Source/AutoVehicleHaul/WarehouseResolver.cs` (new)

- [ ] Method `IntVec3? FindBestWarehouse(Map map, VehiclePawn vehicle, IEnumerable<Thing> cargo)`
- [ ] Scan `map.zoneManager.ZoneListFor(ZoneType.Zone_Stockpile)`
- [ ] Filter by `Zone_Stockpile.GetStoreSettings().AllowedToAccept(thingDef)`
- [ ] Filter by remaining capacity (zone not full)
- [ ] Filter by player faction ownership
- [ ] Filter by pathability (vehicle can reach at least one cell of zone)
- [ ] Score by distance to vehicle + remaining capacity + saturation penalty
- [ ] Fallback: `map.homeArea.CenterCell` or player faction home

### Task 7b-2: Implement MoveToWarehouse state

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] `TickMoveToWarehouse(ctx)`:
  - [ ] On first tick in state: resolve warehouse position via `WarehouseResolver`
  - [ ] Use arrival predicate: `vehicle.Position.DistanceToSquared(target) <= 4`
  - [ ] If NOT arrived AND `ctx.LastIssuedPathTarget != ctx.TargetWarehousePos`:
    - [ ] Call `StartPath(ctx.TargetWarehousePos)`
    - [ ] Set `ctx.LastIssuedPathTarget = ctx.TargetWarehousePos`
  - [ ] If arrival predicate met:
    - [ ] Anti-oscillation check
    - [ ] Transition to `Unloading`

### Task 7b-3: Implement Unloading state

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] `TickUnloading(ctx)`:
  - [ ] **On entry** (first tick in state):
    - [ ] If `ctx.DriverPresence == OnVehicle`: disembark, set `ctx.DriverPresence = OnMap`
  - [ ] If `!DriverExists(ctx)`: transition to `FailSafe`
  - [ ] If `inventory.innerContainer.Count == 0`:
    - [ ] Re-embark driver
    - [ ] Transition to `IdleScan`
    - [ ] Remove from `jobContexts`
  - [ ] If items remain:
    - [ ] Pick next item from innerContainer
    - [ ] If item is null/destroyed: remove from container, continue
    - [ ] Move driver adjacent to vehicle
    - [ ] Remove item from innerContainer
    - [ ] `GenSpawn.Spawn(item, stockpileCell, map)`
  - [ ] Timeout guard (max 600 ticks in Unloading → FailSafe)

### Task 7b-4: Build and test Phase 7b

- [ ] `dotnet build -c Debug` — zero errors
- [ ] Test full cycle: IdleScan → MoveToPickup → Loading → MoveToWarehouse → Unloading → IdleScan
- [ ] Verify items appear in stockpile after unloading
- [ ] Verify vehicle returns to idle scanning

---

## Phase 7c: Polish + Edge Cases

### Task 7c-1: Timeout and stuck detection

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] Add `const int MaxLoadingTicks = 600;`, `MaxUnloadingTicks = 600;`, `MaxStuckTicks = 300;`
- [ ] In Loading sub-states: if sub-state ticks exceed threshold → Failed
- [ ] In MoveToPickup/MoveToWarehouse: if not arrived in 600 ticks → FailSafe

### Task 7c-2: Driver death/injury handling

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] In FSMTick: if `!DriverExists(ctx)`: set `ctx.DriverPresence = None`, transition → FailSafe
- [ ] If driver downed during haul: set `ctx.DriverPresence = None`, transition → FailSafe

### Task 7c-3: Vehicle destruction handling

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] In CleanupFinishedJobs: check `vehicle.Destroyed || !vehicle.Spawned`, remove from jobContexts
- [ ] If vehicle destroyed mid-transit: transition → FailSafe (which handles cleanup)

### Task 7c-4: Capacity-aware haul list

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] In BuildCargoPlan: estimate total mass/count vs vehicle capacity
- [ ] In Loading PickingUp sub-state: recheck capacity before each item
- [ ] Stop loading when `innerContainer.Count >= maxCapacity` (use a reasonable default like 50 or query vehicle def)

### Task 7c-5: Reservation cleanup edge cases

**File:** `Source/AutoVehicleHaul/AutoVehicleHaulManager.cs`

- [ ] In FailSafe: release reservations for all items where `reservedBy[item] == ctx`
- [ ] Null-check: if `ctx.Plan` is default (empty), skip reservation cleanup
- [ ] In normal Loading completion: reservations already removed during transfer, but verify no leaks

### Task 7c-6: Build and test Phase 7c

- [ ] `dotnet build -c Debug` — zero errors
- [ ] Test edge cases: driver downed, vehicle destroyed, no stockpile, full inventory
- [ ] Test FailSafe recovery: verify reservations released, vehicle undrafted, cooldown works
- [ ] Verify graceful recovery in all cases
