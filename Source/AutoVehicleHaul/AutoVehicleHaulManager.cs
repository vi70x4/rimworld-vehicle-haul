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

            var vehicles = map.mapPawns.AllPawnsSpawned.Where(p => p is VehiclePawn).Cast<VehiclePawn>().ToList();

            int idleCount = 0;
            var idleVehicles = new List<VehiclePawn>();

            foreach (var vehicle in vehicles)
            {
                string patherStatus = vehicle.vehiclePather == null ? "null pather" : vehicle.vehiclePather.Moving.ToString();

                Log.Message($"[AutoVehicleHaul] Vehicle: {vehicle.LabelCap} | Pos: {vehicle.Position} | Moving: {patherStatus} | CanMove: {vehicle.CanMove} | Aboard: {vehicle.AllPawnsAboard.Count}");

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

            string bestLabel = null;
            string bestVehicleLabel = null;
            float bestScore = float.MinValue;

            var topCandidates = new List<(Thing thing, VehiclePawn vehicle, float score)>();

            foreach (var thing in map.listerThings.AllThings)
            {
                if (!thing.Spawned)
                    continue;

                if (thing.def.category != ThingCategory.Item)
                    continue;

                if (thing.stackCount <= 0)
                    continue;

                float mass = thing.GetStatValue(StatDefOf.Mass);
                if (mass < 25f)
                    continue;

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

            if (bestLabel != null)
            {
                Log.Message($"[AutoVehicleHaul] Best Candidate: {bestLabel} | Vehicle: {bestVehicleLabel} | Score: {bestScore:F1}");
            }

            Log.Message($"[AutoVehicleHaul] Candidates After Filter: {candidateCount}");
            Log.Message($"[AutoVehicleHaul] Candidates found: {candidateCount}");
        }
    }
}
