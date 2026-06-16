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

                if (nearest != null)
                {
                    Log.Message($"[AutoVehicleHaul] Candidate: {thing.LabelCap} | Mass: {mass:F0} | Closest Vehicle: {nearest.LabelCap} | DistSq: {bestDistSq}");
                    candidateCount++;
                }
            }

            Log.Message($"[AutoVehicleHaul] Candidates scanned: {candidateCount}");
        }
    }
}
