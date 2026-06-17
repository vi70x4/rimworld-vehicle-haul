using System.Collections.Generic;
using System.Linq;
using Verse;
using Vehicles;

namespace AutoVehicleHaul
{
    public enum VehicleState
    {
        IdleScan,
        MoveToPickup,
        Loading,
        MoveToWarehouse,
        Unloading,
        FailSafe
    }

    public enum DriverPresence
    {
        None,
        OnVehicle,
        OnMap
    }

    public enum DriverRole
    {
        Idle,
        Driving,
        Working
    }

    public enum LoadingSubState
    {
        Idle,
        Reserving,
        MovingToItem,
        PickingUp,
        StoringInVehicle,
        ItemDone,
        Failed
    }

    public class VehicleJobContext
    {
        public VehicleState State;
        public VehicleState LastState;
        public int TicksSinceLastTransition;
        public VehiclePawn Vehicle;
        public Pawn DriverPawn;
        public DriverPresence DriverPresence;
        public DriverRole DriverRole;
        public IntVec3 TargetPickupPos;
        public IntVec3 TargetWarehousePos;
        public IntVec3 LastIssuedPathTarget;
        public CargoPlan Plan;
        public LoadingSubState SubState;
        public int SubStateTicks;
        public int CurrentItemIndex;
        public int TransferredCount;
        public int TicksInState;
        public int CurrentJobTimeout;
        public int FailSafeCooldown;
        public IntVec3 LastMovePos;
        public int StuckTicks;
        public bool WeDraftedDriver;
    }

    public class CargoPlan
    {
        public List<HaulItem> Items { get; }
        public VehiclePawn TargetVehicle { get; }
        public IntVec3 PickupPos { get; }
        public IntVec3 WarehousePos { get; }
        public int TotalCount { get; }
        public int TotalMass { get; }

        public CargoPlan(List<HaulItem> items, VehiclePawn vehicle, IntVec3 pickup, IntVec3 warehouse)
        {
            Items = items;
            TargetVehicle = vehicle;
            PickupPos = pickup;
            WarehousePos = warehouse;
            TotalCount = items.Count;
            TotalMass = (int)items.Sum(i => i.Mass);
        }
    }

    public readonly record struct HaulItem(Thing Item, IntVec3 Position, float Mass);
}
