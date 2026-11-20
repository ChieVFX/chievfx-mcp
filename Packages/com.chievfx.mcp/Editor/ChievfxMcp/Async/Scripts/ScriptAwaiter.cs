#nullable enable
using System;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Chievfx.Mcp.Editor
{
    /// <summary>
    /// The awaiter of whatever a script returned that can be awaited (Task, ValueTask, UniTask, Awaitable): found by
    /// the GetAwaiter/IsCompleted/GetResult pattern the compiler itself uses, so no awaitable type is referenced.
    /// Polled from the editor tick instead of awaited: blocking the main thread on it would deadlock the editor,
    /// since the method's continuations run on that same thread.
    /// </summary>
    internal sealed class ScriptAwaiter
    {
        private readonly object awaiter;
        private readonly PropertyInfo isCompletedProperty;
        private readonly MethodInfo getResultMethod;

        private ScriptAwaiter(object awaiter, PropertyInfo isCompletedProperty, MethodInfo getResultMethod)
        {
            this.awaiter = awaiter;
            this.isCompletedProperty = isCompletedProperty;
            this.getResultMethod = getResultMethod;
        }

        public bool ReturnsValue => getResultMethod.ReturnType != typeof(void);

        public bool IsCompleted => isCompletedProperty.GetValue(awaiter) is true;

        // declaredType first: an async Task method returns a Task<VoidTaskResult> at run time, whose awaiter would
        // hand back that internal struct as if the method had a result.
        public static ScriptAwaiter? TryCreate(object? value, Type? declaredType = null)
        {
            if (value == null)
            {
                return null;
            }

            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public;
            var getAwaiter = (declaredType != null && declaredType.IsInstanceOfType(value)
                    ? declaredType.GetMethod("GetAwaiter", instance, null, Type.EmptyTypes, null)
                    : null)
                ?? value.GetType().GetMethod("GetAwaiter", instance, null, Type.EmptyTypes, null);
            if (getAwaiter == null)
            {
                return null;
            }

            var awaiter = getAwaiter.Invoke(value, null);
            if (awaiter == null)
            {
                return null;
            }

            var awaiterType = awaiter.GetType();
            var isCompleted = awaiterType.GetProperty("IsCompleted", instance);
            var getResult = awaiterType.GetMethod("GetResult", instance, null, Type.EmptyTypes, null);
            if (isCompleted == null || isCompleted.PropertyType != typeof(bool) || getResult == null)
            {
                return null;
            }

            return new ScriptAwaiter(awaiter, isCompleted, getResult);
        }

        // Call once, after IsCompleted: pooled awaitables (UniTask) return their source to the pool here.
        public object? GetResult()
        {
            try
            {
                return getResultMethod.Invoke(awaiter, null);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
