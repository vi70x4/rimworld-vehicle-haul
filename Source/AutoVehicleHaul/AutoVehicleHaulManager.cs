using UnityEngine;
using Verse;
using Vehicles;
using System.Linq;
using RimWorld;
using System.Collections.Generic;

namespace AutoVehicleHaul
{
    public class AutoVehicleHaulManager : MapComponent
    {
        /// <summary>
        /// Tracks active haul jobs: vehicle → job context.
        /// </summary>
        private static readonly Dictionary<VehiclePawn, VehicleJobContext> jobContexts = new();

        /// <summary>
        /// Tracks which job context has reserved which item, to prevent double-pickup.
        /// </summary>
        private static readonly Dictionary<Thing, VehicleJobContext> reservedBy = new();

        public AutoVehicleHaulManager(Map map) : base(map)
        {
            Log.Message("[AutoVehicleHaul] Constructor called");
        }

        public override void MapComponentTick()
        {
            // Always: cleanup destroyed/null vehicles
            CleanupFinishedJobs();

            // Every 250 ticks: scan + dispatch (IdleScan state)
            if (Find.TickManager.TicksGame % 250 == 0)
            {
                FullScanAndDispatch();
            }

            // Every 60 ticks: FSM tick (if any active jobs)
            if (Find.TickManager.TicksGame % 60 == 0 && jobContexts.Count > 0)
            {
                foreach (var kvp in jobContexts.ToList())
                {
                    FSMTick(kvp.Value);
                }
            }
        }

        // ─────────────────────────────────────────────
        //  FSM Dispatcher
        // ─────────────────────────────────────────────

        private void FSMTick(VehicleJobContext ctx)
        {
            ctx.TicksInState++;
            ctx.TicksSinceLastTransition++;

            if (!IsDriverStateValid(ctx))
            {
                Log.Message($"[AutoVehicleHaul] WARNING: Driver state invalid for {ctx.Vehicle?.LabelCap}, state={ctx.State}. Transitioning to FailSafe.");
                TransitionState(ctx, VehicleState.FailSafe);
                return;
            }

            switch (ctx.State)
            {
                case VehicleState.IdleScan:
                    TickIdleScan(ctx);
                    break;
                case VehicleState.MoveToPickup:
                    TickMoveToPickup(ctx);
                    break;
                case VehicleState.Loading:
                    TickLoading(ctx);
                    break;
                case VehicleState.MoveToWarehouse:
                    TickMoveToWarehouse(ctx);
                    break;
                case VehicleState.Unloading:
                    TickUnloading(ctx);
                    break;
                case VehicleState.FailSafe:
                    TickFailSafe(ctx);
                    break;
            }
        }

        // ─────────────────────────────────────────────
        //  IdleScan — wraps existing scan logic
        // ─────────────────────────────────────────────

        private void TickIdleScan(VehicleJobContext ctx)
        {
            // IdleScan contexts are not expected to persist between ticks;
            // the scan + dispatch happens in FullScanAndDispatch().
            // If we somehow end up here, transition back to IdleScan (no-op).
            TransitionState(ctx, VehicleState.IdleScan);
        }

        /// <summary>
        /// Full scan: find idle vehicles, find haul-designated items, score, dispatch best pair.
        /// This is the IdleScan state logic, called every 250 ticks.
        /// </summary>
        private void FullScanAndDispatch()
        {
            Log.Message("[AutoVehicleHaul] Scan tick running");

            var vehicles = map.mapPawns.AllPawnsSpawned.Where(p => p is VehiclePawn).Cast<VehiclePawn>().ToList();

            int idleCount = 0;
            var idleVehicles = new List<VehiclePawn>();

            foreach (var vehicle in vehicles)
            {
                string patherStatus = vehicle.vehiclePather == null ? "null pather" : vehicle.vehiclePather.Moving.ToString();
                Log.Message($"[AutoVehicleHaul] Vehicle: {vehicle.LabelCap} | Pos: {vehicle.Position} | Moving: {patherStatus} | CanMove: {vehicle.CanMove} | CanMoveFinal: {vehicle.CanMoveFinal} | Aboard: {vehicle.AllPawnsAboard.Count}");

                if (vehicle.vehiclePather != null && !vehicle.vehiclePather.Moving && vehicle.CanMove)
                {
                    idleCount++;
                    idleVehicles.Add(vehicle);
                }
            }

            Log.Message($"[AutoVehicleHaul] Total vehicles: {vehicles.Count} | Idle: {idleCount}");

            int candidateCount = 0;
            int filteredCount = 0;
            int totalHaulableFound = 0;
            int totalDesignatedForHaul = 0;

            var topCandidates = new List<(Thing thing, VehiclePawn vehicle, float score)>();

            // Phase 5 Diagnostic: Enumerate all designations on haulable things
            int designationCount = 0;

            foreach (var thing in map.listerThings.AllThings)
            {
                if (!thing.Spawned)
                    continue;

                if (thing.def.category != ThingCategory.Item)
                    continue;

                if (thing.stackCount <= 0)
                    continue;

                if (!thing.def.EverHaulable)
                    continue;

                totalHaulableFound++;

                var designations = map.designationManager.AllDesignationsOn(thing);
                foreach (var des in designations)
                {
                    designationCount++;
                    Log.Message($"[AutoVehicleHaul] DESIGNATION | {thing.LabelCap} | {des.def.defName}");
                }
            }

            Log.Message($"[AutoVehicleHaul] Total Designations Found: {designationCount}");

            // Phase 5: Scan all things, filter by EverHaulable + haul designation + not reserved
            foreach (var thing in map.listerThings.AllThings)
            {
                if (!thing.Spawned)
                    continue;

                if (thing.def.category != ThingCategory.Item)
                    continue;

                if (thing.stackCount <= 0)
                    continue;

                if (!thing.def.EverHaulable)
                    continue;

                if (map.designationManager.DesignationOn(thing, DesignationDefOf.Haul) == null)
                    continue;

                // Skip items already reserved by another job
                if (reservedBy.ContainsKey(thing))
                    continue;

                totalDesignatedForHaul++;

                float mass = thing.GetStatValue(StatDefOf.Mass);

                // Mass filter: items under 25kg must have explicit haul designation (already checked above)
                if (mass < 25f)
                {
                    var haulDes = map.designationManager.DesignationOn(thing, DesignationDefOf.Haul);
                    if (haulDes == null)
                        continue;
                }

                if (idleVehicles.Count == 0)
                    continue;

                // Filter out vehicles that already have an active job
                var availableVehicles = idleVehicles.Where(v => !jobContexts.ContainsKey(v)).ToList();
                if (availableVehicles.Count == 0)
                    continue;

                VehiclePawn nearest = null;
                int bestDistSq = int.MaxValue;

                foreach (var v in availableVehicles)
                {
                    int distSq = v.Position.DistanceToSquared(thing.Position);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        nearest = v;
                    }
                }

                if (nearest == null)
                    continue;

                // --- Phase 4: Stabilized Logistics Scoring ---
                float massScore = mass;

                float distance = Mathf.Sqrt(bestDistSq);
                float distancePenalty = distance * 0.5f;

                float resourceBonus = 0f;
                string defName = thing.def.defName.ToLower();
                if (defName.Contains("slag") || defName.Contains("steel") || defName.Contains("component"))
                {
                    resourceBonus = 15f;
                }
                else if (defName.Contains("corpse") || defName.Contains("rotted") || defName.Contains("trash"))
                {
                    resourceBonus = -10f;
                }
                else if (defName.Contains("wood") || defName.Contains("stone"))
                {
                    resourceBonus = 5f;
                }

                float vehicleFit = nearest.RaceProps.baseBodySize * 10f;

                float finalScore = massScore - distancePenalty + resourceBonus + vehicleFit;

                // Clamp score to prevent extreme outliers
                finalScore = Mathf.Clamp(finalScore, -100f, 200f);

                // Minimum viability threshold
                if (finalScore < -50f)
                {
                    filteredCount++;
                    continue;
                }

                candidateCount++;
                topCandidates.Add((thing, nearest, finalScore));
            }

            // Sort top candidates descending by score, take top 5
            topCandidates.Sort((a, b) => b.score.CompareTo(a.score));
            var top5 = topCandidates.Take(5).ToList();

            if (top5.Count > 0)
            {
                Log.Message("[AutoVehicleHaul] TOP CANDIDATES:");
                for (int i = 0; i < top5.Count; i++)
                {
                    var entry = top5[i];
                    Log.Message($"[AutoVehicleHaul] {i + 1}. {entry.thing.LabelCap} | Vehicle: {entry.vehicle.LabelCap} | Score: {entry.score:F1}");
                }
            }

            // --- Phase 6: Vehicle Dispatch with Real Driver Assignment ---
            Thing bestThing = null;
            VehiclePawn bestVehicle = null;

            if (top5.Count > 0)
            {
                bestThing = top5[0].thing;
                bestVehicle = top5[0].vehicle;
            }

            if (bestThing != null && bestVehicle != null)
            {
                Log.Message($"[AutoVehicleHaul] === DISPATCH ===");
                Log.Message($"[AutoVehicleHaul] Target: {bestThing.LabelCap} | Pos: {bestThing.Position}");
                Log.Message($"[AutoVehicleHaul] Vehicle: {bestVehicle.LabelCap} | Pos: {bestVehicle.Position}");

                // --- Phase 6a: Prerequisites Diagnostics ---
                bool drafted = bestVehicle.ignition != null && bestVehicle.ignition.Drafted;
                bool canMove = bestVehicle.CanMove;
                bool canMoveFinal = bestVehicle.CanMoveFinal;
                bool hasEnoughOperators = bestVehicle.HasEnoughOperators;
                int handlerCount = bestVehicle.handlers != null ? bestVehicle.handlers.Count : 0;
                int movementHandlerCount = 0;
                VehicleRoleHandler movementRoleHandler = null;
                if (bestVehicle.handlers != null)
                {
                    foreach (var handler in bestVehicle.handlers)
                    {
                        if (handler != null && (handler.role.HandlingTypes & HandlingType.Movement) != 0)
                        {
                            movementHandlerCount++;
                            movementRoleHandler = handler;
                            bool roleFulfilled = handler.RoleFulfilled;
                            int slotsToOperate = handler.role.SlotsToOperate;
                            int thingOwnerCount = handler.thingOwner != null ? handler.thingOwner.Count : 0;
                            Log.Message($"[AutoVehicleHaul]   Movement Handler: {handler.role.key} | SlotsToOperate: {slotsToOperate} | Occupied: {thingOwnerCount} | RoleFulfilled: {roleFulfilled}");
                        }
                    }
                }

                Log.Message($"[AutoVehicleHaul] Prerequisites: Drafted={drafted} | CanMove={canMove} | CanMoveFinal={canMoveFinal} | HasEnoughOperators={hasEnoughOperators} | Handlers={handlerCount} | MovementHandlers={movementHandlerCount}");

                Pawn driver = null;

                // --- Phase 6b: Assign Driver ---
                if (!hasEnoughOperators && movementRoleHandler != null)
                {
                    Log.Message("[AutoVehicleHaul] No operator assigned. Finding a driver...");
                    driver = FindBestDriver(bestVehicle, map);
                    if (driver != null)
                    {
                        Log.Message($"[AutoVehicleHaul] Selected driver: {driver.LabelCap} | Pos: {driver.Position}");
                        bool assigned = bestVehicle.TryAddPawn(driver, movementRoleHandler);
                        if (assigned)
                        {
                            Log.Message($"[AutoVehicleHaul] Driver {driver.LabelCap} assigned to {bestVehicle.LabelCap}.");
                            Log.Message($"[AutoVehicleHaul] HasEnoughOperators={bestVehicle.HasEnoughOperators} | CanMoveFinal={bestVehicle.CanMoveFinal} | Aboard={bestVehicle.AllPawnsAboard.Count}");
                        }
                        else
                        {
                            Log.Message($"[AutoVehicleHaul] WARNING: TryAddPawn failed for {driver.LabelCap} on {bestVehicle.LabelCap}.");
                            driver = null;
                        }
                    }
                    else
                    {
                        Log.Message("[AutoVehicleHaul] WARNING: No suitable driver found on map.");
                    }
                }
                else if (hasEnoughOperators)
                {
                    Log.Message("[AutoVehicleHaul] Vehicle already has sufficient operators.");
                    // Find an existing driver from the movement handler
                    if (movementRoleHandler != null && movementRoleHandler.thingOwner != null)
                    {
                        foreach (var pawn in movementRoleHandler.thingOwner)
                        {
                            if (pawn is Pawn p)
                            {
                                driver = p;
                                break;
                            }
                        }
                    }
                }
                else
                {
                    Log.Message("[AutoVehicleHaul] WARNING: No movement handler found on vehicle, cannot assign driver.");
                }

                if (driver == null)
                {
                    Log.Message("[AutoVehicleHaul] No driver available, aborting dispatch.");
                    return;
                }

                // --- Phase 6c: Draft Vehicle ---
                if (!drafted)
                {
                    if (bestVehicle.HasEnoughOperators)
                    {
                        bestVehicle.ignition.Drafted = true;
                        Log.Message($"[AutoVehicleHaul] Vehicle drafted via framework API. Drafted={bestVehicle.ignition.Drafted}");
                    }
                    else
                    {
                        Log.Message("[AutoVehicleHaul] Cannot draft: HasEnoughOperators still false after driver assignment attempt.");
                        return;
                    }
                }
                else
                {
                    Log.Message("[AutoVehicleHaul] Vehicle is already drafted.");
                }

                // --- Build Cargo Plan ---
                CargoPlan plan = BuildCargoPlan(bestVehicle, bestVehicle.Position, 20);

                // --- Create Job Context ---
                var ctx = new VehicleJobContext
                {
                    State = VehicleState.MoveToPickup,
                    LastState = VehicleState.IdleScan,
                    TicksSinceLastTransition = 0,
                    Vehicle = bestVehicle,
                    DriverPawn = driver,
                    DriverPresence = DriverPresence.OnVehicle,
                    DriverRole = DriverRole.Driving,
                    TargetPickupPos = bestThing.Position,
                    TargetWarehousePos = IntVec3.Zero,
                    LastIssuedPathTarget = IntVec3.Zero,
                    Plan = plan,
                    SubState = LoadingSubState.Idle,
                    SubStateTicks = 0,
                    CurrentItemIndex = 0,
                    TransferredCount = 0,
                    TicksInState = 0,
                    CurrentJobTimeout = 600,
                    FailSafeCooldown = 0
                };

                // --- Phase 6d: Dispatch ---
                if (bestVehicle.ignition != null && bestVehicle.ignition.Drafted && bestVehicle.CanMoveFinal)
                {
                    try
                    {
                        var targetInfo = new LocalTargetInfo(bestThing.Position);
                        bestVehicle.vehiclePather.StartPath(targetInfo, Verse.AI.PathEndMode.Touch, true);
                        ctx.LastIssuedPathTarget = bestThing.Position;

                        Log.Message($"[AutoVehicleHaul] StartPath called | Dest: {bestThing.Position} | EndMode: Touch | IgnoreReachability: true");
                        Log.Message($"[AutoVehicleHaul] Vehicle Moving: {bestVehicle.vehiclePather.Moving}");
                        Log.Message($"[AutoVehicleHaul] Vehicle Destination: {bestVehicle.vehiclePather.Destination}");
                        Log.Message($"[AutoVehicleHaul] === DISPATCH SUCCESS ===");

                        jobContexts[bestVehicle] = ctx;
                    }
                    catch (System.Exception ex)
                    {
                        Log.Message($"[AutoVehicleHaul] === DISPATCH FAILED ===");
                        Log.Message($"[AutoVehicleHaul] Exception: {ex.GetType().Name}: {ex.Message}");
                        Log.Message($"[AutoVehicleHaul] StackTrace: {ex.StackTrace}");
                    }
                }
                else
                {
                    Log.Message($"[AutoVehicleHaul] Cannot dispatch: Drafted={bestVehicle.ignition?.Drafted} | CanMoveFinal={bestVehicle.CanMoveFinal}");
                }
            }
            else
            {
                Log.Message("[AutoVehicleHaul] No valid candidate/vehicle pair for dispatch");
            }

            Log.Message($"[AutoVehicleHaul] Haulable Found: {totalHaulableFound}");
            Log.Message($"[AutoVehicleHaul] Designated For Hauling: {totalDesignatedForHaul}");
            Log.Message($"[AutoVehicleHaul] Candidates After Filter: {candidateCount}");
        }

        // ─────────────────────────────────────────────
        //  MoveToPickup
        // ─────────────────────────────────────────────

        private void TickMoveToPickup(VehicleJobContext ctx)
        {
            if (ctx.Vehicle == null || ctx.Vehicle.Destroyed)
            {
                TransitionState(ctx, VehicleState.FailSafe);
                return;
            }

            // Driver death/injury check
            if (!DriverExists(ctx))
            {
                Log.Message("[AutoVehicleHaul] MoveToPickup: Driver missing! Failing safe.");
                TransitionState(ctx, VehicleState.FailSafe);
                return;
            }

            // Undrivered vehicle recovery: if vehicle lost its operator mid-transit
            if (ctx.Vehicle.HasEnoughOperators == false)
            {
                Log.Message("[AutoVehicleHaul] MoveToPickup: Vehicle lost its operator! Failing safe.");
                TransitionState(ctx, VehicleState.FailSafe);
                return;
            }

            // Timeout check
            if (ctx.TicksInState > 600)
            {
                Log.Message($"[AutoVehicleHaul] MoveToPickup timeout (600 ticks). Failing safe.");
                TransitionState(ctx, VehicleState.FailSafe);
                return;
            }
            // Check if vehicle has arrived at pickup position
            if (HasArrived(ctx.Vehicle, ctx.TargetPickupPos))
            {
                Log.Message($"[AutoVehicleHaul] Vehicle {ctx.Vehicle.LabelCap} arrived at pickup {ctx.TargetPickupPos}. Transitioning to Loading.");
                TransitionState(ctx, VehicleState.Loading);
                return;
            }

            // Issue path if not moving or target changed
            if (ctx.Vehicle.vehiclePather != null && !ctx.Vehicle.vehiclePather.Moving)
            {
                if (ctx.LastIssuedPathTarget != ctx.TargetPickupPos)
                {
                    try
                    {
                        var targetInfo = new LocalTargetInfo(ctx.TargetPickupPos);
                        ctx.Vehicle.vehiclePather.StartPath(targetInfo, Verse.AI.PathEndMode.Touch, true);
                        ctx.LastIssuedPathTarget = ctx.TargetPickupPos;
                        Log.Message($"[AutoVehicleHaul] MoveToPickup: StartPath to {ctx.TargetPickupPos}");
                    }
                    catch (System.Exception ex)
                    {
                        Log.Message($"[AutoVehicleHaul] MoveToPickup StartPath failed: {ex.Message}");
                    }
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Loading (with Sub-FSM)
        // ─────────────────────────────────────────────

        private void TickLoading(VehicleJobContext ctx)
        {
            // On entry: disembark driver, build cargo plan
            if (ctx.TicksInState == 0)
            {
                DisembarkDriver(ctx);
                ctx.SubState = LoadingSubState.Reserving;
                ctx.SubStateTicks = 0;
                ctx.CurrentItemIndex = 0;
                ctx.TransferredCount = 0;
                Log.Message($"[AutoVehicleHaul] Loading: Driver disembarked. Starting sub-FSM.");
            }

            // Vehicle destruction check
            if (ctx.Vehicle == null || ctx.Vehicle.Destroyed)
            {
                Log.Message("[AutoVehicleHaul] Loading: Vehicle destroyed! Failing safe.");
                TransitionState(ctx, VehicleState.FailSafe);
                return;
            }

            // Timeout check
            if (ctx.TicksInState >= ctx.CurrentJobTimeout)
            {
                Log.Message($"[AutoVehicleHaul] Loading timeout ({ctx.CurrentJobTimeout} ticks). Completing with {ctx.TransferredCount} items.");
                ReembarkDriver(ctx);
                TransitionState(ctx, VehicleState.MoveToWarehouse);
                return;
            }

            ctx.SubStateTicks++;

            switch (ctx.SubState)
            {
                case LoadingSubState.Idle:
                    // Should not happen; reset to Reserving
                    ctx.SubState = LoadingSubState.Reserving;
                    break;

                case LoadingSubState.Reserving:
                    TickLoading_Reserving(ctx);
                    break;

                case LoadingSubState.MovingToItem:
                    TickLoading_MovingToItem(ctx);
                    break;

                case LoadingSubState.PickingUp:
                    TickLoading_PickingUp(ctx);
                    break;

                case LoadingSubState.StoringInVehicle:
                    TickLoading_StoringInVehicle(ctx);
                    break;

                case LoadingSubState.ItemDone:
                    TickLoading_ItemDone(ctx);
                    break;

                case LoadingSubState.Failed:
                    TickLoading_Failed(ctx);
                    break;
            }
        }

        private void TickLoading_Reserving(VehicleJobContext ctx)
        {
            // Check if we've processed all items
            if (ctx.CurrentItemIndex >= ctx.Plan.Items.Count)
            {
                Log.Message($"[AutoVehicleHaul] Loading: All items processed. Transferred={ctx.TransferredCount}. Moving to warehouse.");
                ReembarkDriver(ctx);
                TransitionState(ctx, VehicleState.MoveToWarehouse);
                return;
            }

            var haulItem = ctx.Plan.Items[ctx.CurrentItemIndex];
            Thing item = haulItem.Item;

            // Check if item still exists
            if (item == null || !item.Spawned)
            {
                Log.Message($"[AutoVehicleHaul] Loading: Item #{ctx.CurrentItemIndex} no longer exists, skipping.");
                ctx.SubState = LoadingSubState.Failed;
                return;
            }

            // Check for reservation conflict
            if (reservedBy.TryGetValue(item, out var existingCtx) && existingCtx != ctx)
            {
                Log.Message($"[AutoVehicleHaul] Loading: Item {item.LabelCap} already reserved by another job, skipping.");
                ctx.SubState = LoadingSubState.Failed;
                return;
            }

            // Reserve the item
            reservedBy[item] = ctx;
            Log.Message($"[AutoVehicleHaul] Loading: Reserved item {item.LabelCap} (#{ctx.CurrentItemIndex + 1}/{ctx.Plan.Items.Count})");
            ctx.SubState = LoadingSubState.MovingToItem;
            ctx.SubStateTicks = 0;
        }

        private void TickLoading_MovingToItem(VehicleJobContext ctx)
        {
            if (!DriverExists(ctx))
            {
                Log.Message("[AutoVehicleHaul] Loading: Driver lost during MovingToItem.");
                ctx.SubState = LoadingSubState.Failed;
                return;
            }

            var haulItem = ctx.Plan.Items[ctx.CurrentItemIndex];
            IntVec3 itemPos = haulItem.Position;

            // Check if driver is adjacent to item (distance squared <= 1 for adjacent, <= 4 for nearby)
            int distSq = ctx.DriverPawn.Position.DistanceToSquared(itemPos);
            if (distSq <= 4)
            {
                Log.Message($"[AutoVehicleHaul] Loading: Driver adjacent to {haulItem.Item.LabelCap}. Picking up.");
                ctx.SubState = LoadingSubState.PickingUp;
                ctx.SubStateTicks = 0;
                return;
            }

            // Move driver toward item
            if (ctx.SubStateTicks % 30 == 0) // Re-path every 30 ticks to avoid stale paths
            {
                ctx.DriverPawn.pather.StartPath(itemPos, Verse.AI.PathEndMode.Touch);
            }

            // Timeout: if stuck for too long
            if (ctx.SubStateTicks > 300)
            {
                Log.Message($"[AutoVehicleHaul] Loading: Driver stuck moving to item {haulItem.Item.LabelCap} for 300 ticks. Failing item.");
                ctx.SubState = LoadingSubState.Failed;
            }
        }

        private void TickLoading_PickingUp(VehicleJobContext ctx)
        {
            if (!DriverExists(ctx))
            {
                Log.Message("[AutoVehicleHaul] Loading: Driver lost during PickingUp.");
                ctx.SubState = LoadingSubState.Failed;
                return;
            }

            var haulItem = ctx.Plan.Items[ctx.CurrentItemIndex];
            Thing item = haulItem.Item;

            if (item == null || !item.Spawned)
            {
                Log.Message("[AutoVehicleHaul] Loading: Item no longer exists during PickingUp.");
                ctx.SubState = LoadingSubState.Failed;
                return;
            }

            // Check if vehicle is full
            if (IsVehicleFull(ctx.Vehicle))
            {
                Log.Message($"[AutoVehicleHaul] Loading: Vehicle {ctx.Vehicle.LabelCap} is full. Item done.");
                ctx.SubState = LoadingSubState.ItemDone;
                return;
            }

            // Try to pick up: DeSpawn + TryAdd to vehicle
            IntVec3 originalPos = item.Position;
            Map itemMap = item.Map;

            if (TryStoreItem(ctx, item, originalPos, itemMap))
            {
                Log.Message($"[AutoVehicleHaul] Loading: {item.LabelCap} stored in vehicle. Transferred={ctx.TransferredCount}");
                ctx.SubState = LoadingSubState.ItemDone;
            }
            else
            {
                Log.Message($"[AutoVehicleHaul] Loading: Failed to store {item.LabelCap} in vehicle. Restored to world.");
                ctx.SubState = LoadingSubState.StoringInVehicle;
                ctx.SubStateTicks = 0;
            }
        }

        private void TickLoading_StoringInVehicle(VehicleJobContext ctx)
        {
            if (!DriverExists(ctx))
            {
                Log.Message("[AutoVehicleHaul] Loading: Driver lost during StoringInVehicle.");
                ctx.SubState = LoadingSubState.Failed;
                return;
            }

            // Move driver to vehicle position
            int distSq = ctx.DriverPawn.Position.DistanceToSquared(ctx.Vehicle.Position);
            if (distSq <= 4)
            {
                // Adjacent to vehicle, try to store
                var haulItem = ctx.Plan.Items[ctx.CurrentItemIndex];
                Thing item = haulItem.Item;

                if (item == null || !item.Spawned)
                {
                    Log.Message("[AutoVehicleHaul] Loading: Item no longer exists during StoringInVehicle.");
                    ctx.SubState = LoadingSubState.Failed;
                    return;
                }

                if (IsVehicleFull(ctx.Vehicle))
                {
                    Log.Message($"[AutoVehicleHaul] Loading: Vehicle full. Item done.");
                    ctx.SubState = LoadingSubState.ItemDone;
                    return;
                }

                IntVec3 originalPos = item.Position;
                Map itemMap = item.Map;

                if (TryStoreItem(ctx, item, originalPos, itemMap))
                {
                    Log.Message($"[AutoVehicleHaul] Loading: {item.LabelCap} stored via StoringInVehicle. Transferred={ctx.TransferredCount}");
                    ctx.SubState = LoadingSubState.ItemDone;
                }
                else
                {
                    Log.Message($"[AutoVehicleHaul] Loading: StoringInVehicle failed for {item.LabelCap}. Item done (will retry next job).");
                    ctx.SubState = LoadingSubState.ItemDone;
                }
                return;
            }

            // Move driver toward vehicle
            if (ctx.SubStateTicks % 30 == 0)
            {
                ctx.DriverPawn.pather.StartPath(ctx.Vehicle.Position, Verse.AI.PathEndMode.Touch);
            }

            // Timeout
            if (ctx.SubStateTicks > 300)
            {
                Log.Message($"[AutoVehicleHaul] Loading: StoringInVehicle timeout. Item done.");
                ctx.SubState = LoadingSubState.ItemDone;
            }
        }

        private void TickLoading_ItemDone(VehicleJobContext ctx)
        {
            // Move to next item
            ctx.CurrentItemIndex++;

            if (ctx.CurrentItemIndex >= ctx.Plan.Items.Count)
            {
                // All items processed
                Log.Message($"[AutoVehicleHaul] Loading: All items done. Transferred={ctx.TransferredCount}. Moving to warehouse.");
                // Release any remaining reservations before transitioning
                ReleaseReservations(ctx);
                ReembarkDriver(ctx);
                TransitionState(ctx, VehicleState.MoveToWarehouse);
            }
            else
            {
                // Process next item
                ctx.SubState = LoadingSubState.Reserving;
                ctx.SubStateTicks = 0;
            }
        }

        private void TickLoading_Failed(VehicleJobContext ctx)
        {
            // Release reservation for failed item
            if (ctx.CurrentItemIndex < ctx.Plan.Items.Count)
            {
                var haulItem = ctx.Plan.Items[ctx.CurrentItemIndex];
                if (haulItem.Item != null)
                {
                    reservedBy.Remove(haulItem.Item);
                }
            }

            // Move to next item
            ctx.CurrentItemIndex++;

            if (ctx.CurrentItemIndex >= ctx.Plan.Items.Count)
            {
                // All items processed (some may have failed)
                Log.Message($"[AutoVehicleHaul] Loading: All items processed (some failed). Transferred={ctx.TransferredCount}. Moving to warehouse.");
                // Release any remaining reservations before transitioning
                ReleaseReservations(ctx);
                ReembarkDriver(ctx);
                TransitionState(ctx, VehicleState.MoveToWarehouse);
            }
            else
            {
                // Process next item
                ctx.SubState = LoadingSubState.Reserving;
                ctx.SubStateTicks = 0;
            }
        }

        // ─────────────────────────────────────────────
        //  MoveToWarehouse
        // ─────────────────────────────────────────────

        private void TickMoveToWarehouse(VehicleJobContext ctx)
        {
            if (ctx.Vehicle == null || ctx.Vehicle.Destroyed)
            {
                TransitionState(ctx, VehicleState.FailSafe);
                return;
            }

            // Driver death/injury check
            if (!DriverExists(ctx))
            {
                Log.Message("[AutoVehicleHaul] MoveToWarehouse: Driver missing! Failing safe.");
                TransitionState(ctx, VehicleState.FailSafe);
                return;
            }

            // Timeout check
            if (ctx.TicksInState > 600)
            {
                Log.Message($"[AutoVehicleHaul] MoveToWarehouse timeout (600 ticks). Failing safe.");
                TransitionState(ctx, VehicleState.FailSafe);
                return;
            }
            // On entry: find warehouse if not set
            if (ctx.TargetWarehousePos == IntVec3.Zero)
            {
                // Find nearest stockpile zone
                var stockpiles = map.zoneManager.AllZones.Where(z => z is Zone_Stockpile).ToList();
                if (stockpiles.Count > 0)
                {
                    // Find closest stockpile cell to vehicle
                    IntVec3 bestCell = IntVec3.Zero;
                    int bestDistSq = int.MaxValue;
                    foreach (var zone in stockpiles)
                    {
                        foreach (var cell in zone.Cells)
                        {
                            int d = cell.DistanceToSquared(ctx.Vehicle.Position);
                            if (d < bestDistSq)
                            {
                                bestDistSq = d;
                                bestCell = cell;
                            }
                        }
                    }
                    ctx.TargetWarehousePos = bestCell;
                    Log.Message($"[AutoVehicleHaul] MoveToWarehouse: Target warehouse set to {bestCell}");
                }
                else
                {
                    Log.Message("[AutoVehicleHaul] MoveToWarehouse: No stockpile found! Failing.");
                    TransitionState(ctx, VehicleState.FailSafe);
                    return;
                }
            }

            // Check if arrived
            if (HasArrived(ctx.Vehicle, ctx.TargetWarehousePos))
            {
                Log.Message($"[AutoVehicleHaul] Vehicle {ctx.Vehicle.LabelCap} arrived at warehouse {ctx.TargetWarehousePos}. Transitioning to Unloading.");
                TransitionState(ctx, VehicleState.Unloading);
                return;
            }

            // Issue path if not moving or target changed
            if (ctx.Vehicle.vehiclePather != null && !ctx.Vehicle.vehiclePather.Moving)
            {
                if (ctx.LastIssuedPathTarget != ctx.TargetWarehousePos)
                {
                    try
                    {
                        var targetInfo = new LocalTargetInfo(ctx.TargetWarehousePos);
                        ctx.Vehicle.vehiclePather.StartPath(targetInfo, Verse.AI.PathEndMode.Touch, true);
                        ctx.LastIssuedPathTarget = ctx.TargetWarehousePos;
                        Log.Message($"[AutoVehicleHaul] MoveToWarehouse: StartPath to {ctx.TargetWarehousePos}");
                    }
                    catch (System.Exception ex)
                    {
                        Log.Message($"[AutoVehicleHaul] MoveToWarehouse StartPath failed: {ex.Message}");
                    }
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Unloading
        // ─────────────────────────────────────────────

        private void TickUnloading(VehicleJobContext ctx)
        {
            // On entry: disembark driver
            if (ctx.TicksInState == 0)
            {
                DisembarkDriver(ctx);
                Log.Message($"[AutoVehicleHaul] Unloading: Driver disembarked.");
            }

            // Vehicle destruction check
            if (ctx.Vehicle == null || ctx.Vehicle.Destroyed)
            {
                Log.Message("[AutoVehicleHaul] Unloading: Vehicle destroyed! Failing safe.");
                TransitionState(ctx, VehicleState.FailSafe);
                return;
            }

            // Safety check: driver must exist
            if (!DriverExists(ctx))
            {
                Log.Message("[AutoVehicleHaul] Unloading: Driver missing! Failing safe.");
                TransitionState(ctx, VehicleState.FailSafe);
                return;
            }

            // Timeout check
            if (ctx.TicksInState >= 600)
            {
                Log.Message("[AutoVehicleHaul] Unloading timeout (600 ticks). Failing safe.");
                TransitionState(ctx, VehicleState.FailSafe);
                return;
            }

            // Check if inventory is empty
            if (ctx.Vehicle.inventory.innerContainer.Count == 0)
            {
                Log.Message($"[AutoVehicleHaul] Unloading: Vehicle inventory empty. Job complete.");
                // Release any remaining reservations before completing
                ReleaseReservations(ctx);
                ReembarkDriver(ctx);
                // Remove job context
                jobContexts.Remove(ctx.Vehicle);
                return;
            }

            // Pick an item from inventory and spawn at warehouse
            // Transfer one item per tick for safety
            Thing item = ctx.Vehicle.inventory.innerContainer[0];
            if (item != null)
            {
                ctx.Vehicle.inventory.innerContainer.Remove(item);
                IntVec3 spawnPos = ctx.TargetWarehousePos;
                // Find a nearby valid cell if warehouse pos is occupied
                if (!spawnPos.Walkable(map))
                {
                    spawnPos = CellFinder.RandomClosewalkCellNear(spawnPos, map, 2);
                }
                GenSpawn.Spawn(item, spawnPos, map);
                Log.Message($"[AutoVehicleHaul] Unloading: Spawned {item.LabelCap} at {spawnPos}. Remaining={ctx.Vehicle.inventory.innerContainer.Count}");
            }
        }

        // ─────────────────────────────────────────────
        //  FailSafe
        // ─────────────────────────────────────────────

        private void TickFailSafe(VehicleJobContext ctx)
        {
            // On entry: release reservations, re-embark driver, undraft, remove context
            // Each step is wrapped in try-catch to prevent cascading failures
            if (ctx.TicksInState == 0)
            {
                Log.Message($"[AutoVehicleHaul] FailSafe entered for {ctx.Vehicle?.LabelCap}. Releasing reservations.");

                // Release all reservations held by this context
                try { ReleaseReservations(ctx); }
                catch (System.Exception ex) { Log.Message($"[AutoVehicleHaul] FailSafe: Exception releasing reservations: {ex.Message}"); }

                // Re-embark driver (skip if driver is null/dead)
                try
                {
                    if (DriverExists(ctx))
                        ReembarkDriver(ctx);
                    else
                        Log.Message("[AutoVehicleHaul] FailSafe: Driver not available, skipping re-embark.");
                }
                catch (System.Exception ex) { Log.Message($"[AutoVehicleHaul] FailSafe: Exception re-embarking driver: {ex.Message}"); }

                // Undraft vehicle (skip if vehicle is null)
                try
                {
                    if (ctx.Vehicle != null && ctx.Vehicle.ignition != null)
                        ctx.Vehicle.ignition.Drafted = false;
                    else
                        Log.Message("[AutoVehicleHaul] FailSafe: Vehicle or ignition null, skipping undraft.");
                }
                catch (System.Exception ex) { Log.Message($"[AutoVehicleHaul] FailSafe: Exception undrafting vehicle: {ex.Message}"); }

                // Remove job context
                try
                {
                    if (ctx.Vehicle != null)
                        jobContexts.Remove(ctx.Vehicle);
                }
                catch (System.Exception ex) { Log.Message($"[AutoVehicleHaul] FailSafe: Exception removing job context: {ex.Message}"); }

                ctx.FailSafeCooldown = 250;
                Log.Message($"[AutoVehicleHaul] FailSafe: Cooldown set to {ctx.FailSafeCooldown} ticks.");
            }

            // Decrement cooldown
            ctx.FailSafeCooldown--;

            if (ctx.FailSafeCooldown <= 0)
            {
                Log.Message($"[AutoVehicleHaul] FailSafe cooldown expired. Returning to IdleScan.");
                // Context is already removed from jobContexts; vehicle will be picked up by next scan
            }
        }

        /// <summary>
        /// Try to store an item in the vehicle inventory. Handles DeSpawn + TryAdd + restore-on-fail.
        /// Returns true if successfully stored, false otherwise.
        /// </summary>
        private bool TryStoreItem(VehicleJobContext ctx, Thing item, IntVec3 originalPos, Map itemMap)
        {
            try
            {
                item.DeSpawn();
                bool added = ctx.Vehicle.inventory.innerContainer.TryAdd(item);

                if (added)
                {
                    reservedBy.Remove(item);
                    ctx.TransferredCount++;
                    return true;
                }
                else
                {
                    GenSpawn.Spawn(item, originalPos, itemMap);
                    return false;
                }
            }
            catch (System.Exception ex)
            {
                try { GenSpawn.Spawn(item, originalPos, itemMap); } catch { }
                Log.Message($"[AutoVehicleHaul] Loading: Exception storing {item.LabelCap}: {ex.Message}. Restored to world.");
                return false;
            }
        }

        // ─────────────────────────────────────────────
        //  Helper Methods
        // ─────────────────────────────────────────────

        /// <summary>
        /// Check if vehicle inventory is full (>= 50 items).
        /// </summary>
        private bool IsVehicleFull(VehiclePawn vehicle)
        {
            if (vehicle?.inventory?.innerContainer == null)
                return true;
            return vehicle.inventory.innerContainer.Count >= 50;
        }

        /// <summary>
        /// Check if there are haul-designated items near the vehicle within the given radius.
        /// Filters out items already reserved by another job.
        /// </summary>
        private bool HasNearbyHaulables(VehiclePawn vehicle, int radius)
        {
            if (vehicle == null || !vehicle.Spawned)
                return false;

            IntVec3 pos = vehicle.Position;
            float radiusSq = radius * radius;

            foreach (var thing in map.listerThings.AllThings)
            {
                if (!thing.Spawned)
                    continue;
                if (thing.def.category != ThingCategory.Item)
                    continue;
                if (thing.stackCount <= 0)
                    continue;
                if (!thing.def.EverHaulable)
                    continue;
                if (map.designationManager.DesignationOn(thing, DesignationDefOf.Haul) == null)
                    continue;
                if (reservedBy.ContainsKey(thing))
                    continue;

                if (thing.Position.DistanceToSquared(pos) <= radiusSq)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Build a cargo plan: scan for haul-designated items near the vehicle,
        /// filter out reserved items, score them, and return a readonly CargoPlan.
        /// Note: Warehouse position is resolved later in MoveToWarehouse, not here.
        /// </summary>
        private CargoPlan BuildCargoPlan(VehiclePawn vehicle, IntVec3 pickupPos, int radius)
        {
            var items = new List<HaulItem>();
            float radiusSq = radius * radius;
            float maxCapacity = 500f; // Max total mass per haul trip
            float runningMass = 0f;

            foreach (var thing in map.listerThings.AllThings)
            {
                if (!thing.Spawned)
                    continue;
                if (thing.def.category != ThingCategory.Item)
                    continue;
                if (thing.stackCount <= 0)
                    continue;
                if (!thing.def.EverHaulable)
                    continue;
                if (map.designationManager.DesignationOn(thing, DesignationDefOf.Haul) == null)
                    continue;
                if (reservedBy.ContainsKey(thing))
                    continue;

                if (thing.Position.DistanceToSquared(pickupPos) <= radiusSq)
                {
                    float mass = thing.GetStatValue(StatDefOf.Mass);

                    // Capacity-aware: skip items that would exceed vehicle capacity
                    if (runningMass + mass > maxCapacity)
                        continue;

                    runningMass += mass;
                    items.Add(new HaulItem(thing, thing.Position, mass));
                }
            }

            // Score and sort items by mass (heaviest first for efficient loading)
            items.Sort((a, b) => b.Mass.CompareTo(a.Mass));


            return new CargoPlan(items, vehicle, pickupPos, IntVec3.Zero);
        }

        /// <summary>
        /// Check if vehicle has arrived at the target position (distance squared <= 4).
        /// </summary>
        private bool HasArrived(VehiclePawn vehicle, IntVec3 target)
        {
            if (vehicle == null)
                return false;
            return vehicle.Position.DistanceToSquared(target) <= 4;
        }

        /// <summary>
        /// Check if the driver pawn exists and is alive.
        /// </summary>
        private bool DriverExists(VehicleJobContext ctx)
        {
            return ctx.DriverPawn != null && !ctx.DriverPawn.Dead && !ctx.DriverPawn.Destroyed;
        }

        /// <summary>
        /// Validate driver presence/role consistency.
        /// </summary>
        private bool IsDriverStateValid(VehicleJobContext ctx)
        {
            if (ctx.DriverPawn == null)
                return true; // No driver assigned yet is valid for some states

            bool onVehicle = ctx.DriverPawn.InVehicle();
            bool presenceOk = (ctx.DriverPresence == DriverPresence.OnVehicle && onVehicle) ||
                              (ctx.DriverPresence == DriverPresence.OnMap && !onVehicle) ||
                              (ctx.DriverPresence == DriverPresence.None);

            return presenceOk;
        }

        /// <summary>
        /// Disembark the driver from the vehicle and set presence/role.
        /// </summary>
        private void DisembarkDriver(VehicleJobContext ctx)
        {
            if (ctx.DriverPawn == null)
                return;

            if (ctx.Vehicle != null)
            {
                ctx.Vehicle.DisembarkPawn(ctx.DriverPawn);
            }

            ctx.DriverPresence = DriverPresence.OnMap;
            ctx.DriverRole = DriverRole.Working;
        }

        /// <summary>
        /// Re-embark the driver onto the vehicle and set presence/role.
        /// </summary>
        private void ReembarkDriver(VehicleJobContext ctx)
        {
            if (ctx.DriverPawn == null || ctx.Vehicle == null)
                return;

            // Find the movement handler
            VehicleRoleHandler movementHandler = null;
            if (ctx.Vehicle.handlers != null)
            {
                foreach (var h in ctx.Vehicle.handlers)
                {
                    if (h != null && (h.role.HandlingTypes & HandlingType.Movement) != 0)
                    {
                        movementHandler = h;
                        break;
                    }
                }
            }

            if (movementHandler != null)
            {
                ctx.Vehicle.TryAddPawn(ctx.DriverPawn, movementHandler);
            }

            ctx.DriverPresence = DriverPresence.OnVehicle;
            ctx.DriverRole = DriverRole.Driving;
        }

        /// <summary>
        /// Remove destroyed or null vehicles from jobContexts.
        /// </summary>
        private void CleanupFinishedJobs()
        {
            var toRemove = new List<VehiclePawn>();
            foreach (var kvp in jobContexts)
            {
                VehiclePawn vehicle = kvp.Key;
                if (vehicle == null || vehicle.Destroyed || !vehicle.Spawned)
                {
                    toRemove.Add(vehicle);
                }
            }
            foreach (var v in toRemove)
            {
                // Release reservations for this context
                if (jobContexts.TryGetValue(v, out var ctx))
                {
                    ReleaseReservations(ctx);
                }
                jobContexts.Remove(v);
            }
        }

        /// <summary>
        /// Transition the context to a new state, updating LastState and resetting counters.
        /// </summary>
        private void TransitionState(VehicleJobContext ctx, VehicleState newState)
        {
            // Anti-oscillation guard: prevent transitioning back to the same state too quickly
            if (newState == ctx.LastState && ctx.TicksSinceLastTransition < 120)
            {
                Log.Message($"[AutoVehicleHaul] WARNING: Anti-oscillation guard blocked transition to {newState} (LastState={ctx.LastState}, TicksSinceLastTransition={ctx.TicksSinceLastTransition})");
                return;
            }
            ctx.LastState = ctx.State;
            ctx.State = newState;
            ctx.TicksInState = 0;
            ctx.TicksSinceLastTransition = 0;
        }

        /// <summary>
        /// Release all reservations held by this context.
        /// </summary>
        private void ReleaseReservations(VehicleJobContext ctx)
        {
            var toRelease = reservedBy.Where(kvp => kvp.Value == ctx).Select(kvp => kvp.Key).ToList();
            foreach (var item in toRelease)
            {
                reservedBy.Remove(item);
            }
        }

        /// <summary>
        /// Find the best available colonist to serve as a vehicle driver.
        /// Prioritizes: not downed, not dead, not in mental state, capable of manipulation and consciousness,
        /// spawned on map, same faction, not already in a vehicle. Closest to vehicle by position.
        /// </summary>
        private Pawn FindBestDriver(VehiclePawn vehicle, Map map)
        {
            VehicleRoleHandler movementRoleHandler = null;
            if (vehicle.handlers != null)
            {
                foreach (var h in vehicle.handlers)
                {
                    if (h != null && (h.role.HandlingTypes & HandlingType.Movement) != 0)
                    {
                        movementRoleHandler = h;
                        break;
                    }
                }
            }

            if (movementRoleHandler == null)
            {
                Log.Message("[AutoVehicleHaul] No movement handler found, cannot evaluate drivers.");
                return null;
            }

            Pawn bestDriver = null;
            int bestDistSq = int.MaxValue;

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                // Must be capable of operating the role
                if (!movementRoleHandler.CanOperateRole(pawn))
                    continue;

                // Must not already be in a vehicle
                if (pawn.InVehicle())
                    continue;

                int distSq = pawn.Position.DistanceToSquared(vehicle.Position);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestDriver = pawn;
                }
            }

            return bestDriver;
        }
    }
}
