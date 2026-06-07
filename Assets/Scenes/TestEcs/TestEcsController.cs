using Unity.Entities;
using UnityEngine;

namespace Chievfx.Ecs.Test
{
    public sealed class TestEcsController : MonoBehaviour
    {
        public const string CustomWorldName = "EcsTestCustomWorld";

        [SerializeField] int customEntityCount = 3;

        World customWorld;

        void OnEnable()
        {
            CreateCustomWorld();
        }

        void OnDisable()
        {
            DisposeCustomWorld();
        }

        void CreateCustomWorld()
        {
            customWorld = new World(CustomWorldName);

            customWorld.CreateSystemManaged<InitializationSystemGroup>();
            var simulation = customWorld.CreateSystemManaged<SimulationSystemGroup>();
            customWorld.CreateSystemManaged<PresentationSystemGroup>();

            var incrementSystem = customWorld.CreateSystem<CustomWorldIncrementSystem>();
            simulation.AddSystemToUpdateList(incrementSystem);

            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(customWorld);
            SpawnCustomEntities();
        }

        void SpawnCustomEntities()
        {
            var entityManager = customWorld.EntityManager;
            var count = Mathf.Max(1, customEntityCount);

            for (var i = 0; i < count; i++)
            {
                var entity = entityManager.CreateEntity(
                    typeof(CustomWorldTestTag),
                    typeof(TestCounter),
                    typeof(TestVelocity));

                entityManager.SetComponentData(entity, new TestCounter { Value = 10 + i });
                entityManager.SetComponentData(entity, new TestVelocity { Speed = 0.5f + i });
            }
        }

        void DisposeCustomWorld()
        {
            if (customWorld == null || !customWorld.IsCreated)
            {
                return;
            }

            if (ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(customWorld))
            {
                ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(customWorld);
            }

            customWorld.Dispose();
            customWorld = null;
        }
    }
}
