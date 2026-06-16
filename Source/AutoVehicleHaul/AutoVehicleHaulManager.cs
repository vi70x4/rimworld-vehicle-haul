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
        /// Tracks which pawn was assigned as driver for which vehicle, so we can disembark on completion.
        /// </summary>
        private static readonly Dictionary<VehiclePawn, Pawn> assignedDrivers = new Dictionary<VehiclePawn, Pawn>();

        public AutoVehicleHaulManager(Map map) : base(map)
        {
            Log.Message("[AutoVehicleHaul] Constructor called");
        }

        public override void MapComponentTick()
        {
            if (Find.TickManager.TicksGame % 250 != 0)
            {
                return;
            }
            Log.Message("[AutoVehicleHaul] Scan tick running");

            // --- Cleanup: disembark drivers from vehicles that are no longer moving ---
            var toRemove = new List<VehiclePawn>();
            foreach (var kvp in assignedDrivers)
            {
                VehiclePawn vehicle = kvp.Key;
                Pawn driver = kvp.Value;
                if (vehicle == null || vehicle.Destroyed || !vehicle.Spawned)
                {
                    toRemove.Add(vehicle);
                    continue;
                }
                // Once vehicle reaches destination (no longer moving), disembark driver
                if (!vehicle.vehiclePather.Moving)
                {
                    Log.Message($"[AutoVehicleHaul] Vehicle {vehicle.LabelCap} stopped moving. Disembarking driver {driver.LabelCap}.");
                    vehicle.DisembarkPawn(driver);
                    toRemove.Add(vehicle);
                }
            }
            foreach (var v in toRemove)
            {
                assignedDrivers.Remove(v);
            }

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
            Log.Message($"[AutoVehicleHaul] Idle vehicles found: {idleVehicles.Count}");

            int candidateCount = 0;
            int filteredCount = 0;
            int totalHaulableFound = 0;
            int totalDesignatedForHaul = 0;

            string bestLabel = null;
            string bestVehicleLabel = null;
            float bestScore = float.MinValue;

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

            // Phase 5: Scan all things, filter by EverHaulable + haul designation
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

                totalDesignatedForHaul++;

                float mass = thing.GetStatValue(StatDefOf.Mass);

                // Designated haul items always qualify; mass filter only for auto-scan
                if (mass < 25f)
                {
                    var haulDes = map.designationManager.DesignationOn(thing, DesignationDefOf.Haul);
                    if (haulDes == null)
                        continue;
                }

                if (idleVehicles.Count == 0)
                    continue;

                VehiclePawn nearest = null;
                int bestDistSq = int.MaxValue;

                foreach (var v in idleVehicles)
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

                // Track top 5 candidates
                topCandidates.Add((thing, nearest, finalScore));

                if (finalScore > bestScore)
                {
                    bestScore = finalScore;
                    bestLabel = thing.LabelCap;
                    bestVehicleLabel = nearest.LabelCap;
                }
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

                // --- Phase 6b: Assign Driver ---
                // Instead of force-drafting via reflection, we assign a real colonist to the
                // vehicle's movement handler. This satisfies VehicleFramework's invariants:
                //   HasEnoughOperators → true (real pawn in driver role)
                //   CanMoveFinal → true (CanMove && HasEnoughOperators)
                //   CanDraft() → true (HasEnoughOperators satisfied)
                //   StartPath → succeeds (Drafted check passes)
                //   PatherTick → continues (CanMoveFinal check passes)
                if (!hasEnoughOperators && movementRoleHandler != null)
                {
                    Log.Message("[AutoVehicleHaul] No operator assigned. Finding a driver...");
                    Pawn driver = FindBestDriver(bestVehicle, map);
                    if (driver != null)
                    {
                        Log.Message($"[AutoVehicleHaul] Selected driver: {driver.LabelCap} | Pos: {driver.Position}");
                        bool assigned = bestVehicle.TryAddPawn(driver, movementRoleHandler);
                        if (assigned)
                        {
                            Log.Message($"[AutoVehicleHaul] Driver {driver.LabelCap} assigned to {bestVehicle.LabelCap}.");
                            Log.Message($"[AutoVehicleHaul] HasEnoughOperators={bestVehicle.HasEnoughOperators} | CanMoveFinal={bestVehicle.CanMoveFinal} | Aboard={bestVehicle.AllPawnsAboard.Count}");
                            // Track for cleanup
                            assignedDrivers[bestVehicle] = driver;
                        }
                        else
                        {
                            Log.Message($"[AutoVehicleHaul] WARNING: TryAddPawn failed for {driver.LabelCap} on {bestVehicle.LabelCap}.");
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
                }
                else
                {
                    Log.Message("[AutoVehicleHaul] WARNING: No movement handler found on vehicle, cannot assign driver.");
                }

                // --- Phase 6c: Draft Vehicle ---
                // Now that HasEnoughOperators is satisfied, CanDraft() will succeed.
                // We draft normally through the framework API — no reflection needed.
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
                    }
                }
                else
                {
                    Log.Message("[AutoVehicleHaul] Vehicle is already drafted.");
                }

                // --- Phase 6d: Dispatch ---
                if (bestVehicle.ignition != null && bestVehicle.ignition.Drafted && bestVehicle.CanMoveFinal)
                {
                    try
                    {
                        var targetInfo = new LocalTargetInfo(bestThing.Position);
                        bestVehicle.vehiclePather.StartPath(targetInfo, Verse.AI.PathEndMode.Touch, true);

                        Log.Message($"[AutoVehicleHaul] StartPath called | Dest: {bestThing.Position} | EndMode: Touch | IgnoreReachability: true");
                        Log.Message($"[AutoVehicleHaul] Vehicle Moving: {bestVehicle.vehiclePather.Moving}");
                        Log.Message($"[AutoVehicleHaul] Vehicle Destination: {bestVehicle.vehiclePather.Destination}");
                        Log.Message($"[AutoVehicleHaul] === DISPATCH SUCCESS ===");
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
