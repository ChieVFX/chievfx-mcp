using Unity.Entities;

namespace Chievfx.Ecs.Test
{
    public struct TestCounter : IComponentData
    {
        public int Value;
    }

    public struct TestVelocity : IComponentData
    {
        public float Speed;
    }

    public struct DefaultWorldTestTag : IComponentData
    {
    }

    public struct CustomWorldTestTag : IComponentData
    {
    }
}
