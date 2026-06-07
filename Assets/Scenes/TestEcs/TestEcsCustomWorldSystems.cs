using Unity.Entities;

namespace Chievfx.Ecs.Test
{
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct CustomWorldIncrementSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (counter, velocity) in SystemAPI.Query<RefRW<TestCounter>, RefRO<TestVelocity>>().WithAll<CustomWorldTestTag>())
            {
                counter.ValueRW.Value += (int)(velocity.ValueRO.Speed * 2f);
            }
        }
    }
}
