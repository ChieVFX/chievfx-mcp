using Unity.Entities;

namespace Chievfx.Ecs.Test
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct DefaultWorldSpawnSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            var entityManager = state.EntityManager;

            for (var i = 0; i < 3; i++)
            {
                var entity = entityManager.CreateEntity(
                    typeof(DefaultWorldTestTag),
                    typeof(TestCounter),
                    typeof(TestVelocity));

                entityManager.SetComponentData(entity, new TestCounter { Value = 100 + i });
                entityManager.SetComponentData(entity, new TestVelocity { Speed = 1f + i });
            }
        }

        public void OnUpdate(ref SystemState state)
        {
        }
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct DefaultWorldIncrementSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (counter, velocity) in SystemAPI.Query<RefRW<TestCounter>, RefRO<TestVelocity>>().WithAll<DefaultWorldTestTag>())
            {
                counter.ValueRW.Value += (int)velocity.ValueRO.Speed;
            }
        }
    }
}
