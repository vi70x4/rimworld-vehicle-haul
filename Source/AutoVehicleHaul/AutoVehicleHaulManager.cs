using Verse;
using Vehicles;
using System.Linq;
using RimWorld;

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

            var vehicles = map.mapPawns.AllPawnsSpawned.Where(p => p is VehiclePawn).Cast<VehiclePawn>().ToList();

            int idleCount = 0;
            var idleVehicles = new System.Collections.Generic.List<VehiclePawn>();

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

            int candidateCount = 0;

            string bestLabel = null;
            string bestVehicleLabel = null;
            float bestScore = float.MinValue;

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

                // --- Phase 3: Logistics Scoring ---
                float massScore = mass;
                float distancePenalty = bestDistSq * 0.05f;

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

                Log.Message($"[AutoVehicleHaul] Candidate: {thing.LabelCap} | Vehicle: {nearest.LabelCap} | Score: {finalScore:F1} | DistSq: {bestDistSq}");

                if (finalScore > bestScore)
                {
                    bestScore = finalScore;
                    bestLabel = thing.LabelCap;
                    bestVehicleLabel = nearest.LabelCap;
                }

                candidateCount++;
            }

            if (bestLabel != null)
            {
                Log.Message($"[AutoVehicleHaul] Best Candidate: {bestLabel} | Vehicle: {bestVehicleLabel} | Score: {bestScore:F1}");
            }

            Log.Message($"[AutoVehicleHaul] Candidates scanned: {candidateCount}");
        }
    }
}
