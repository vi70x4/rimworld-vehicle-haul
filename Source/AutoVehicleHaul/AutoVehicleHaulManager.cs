using Verse;
using Vehicles;
using System.Linq;

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

            foreach (var vehicle in vehicles)
            {
                string patherStatus = vehicle.vehiclePather == null ? "null pather" : vehicle.vehiclePather.Moving.ToString();

                Log.Message($"[AutoVehicleHaul] Vehicle: {vehicle.LabelCap} | Pos: {vehicle.Position} | Moving: {patherStatus} | CanMove: {vehicle.CanMove} | Aboard: {vehicle.AllPawnsAboard.Count}");

                if (vehicle.vehiclePather != null && !vehicle.vehiclePather.Moving && vehicle.CanMove)
                {
                    idleCount++;
                }
            }

            Log.Message($"[AutoVehicleHaul] Total vehicles: {vehicles.Count} | Idle: {idleCount}");
        }
    }
}
