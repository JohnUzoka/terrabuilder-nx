using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace TerrariaHooks
{
    public static class LoweredHookRegistry
    {
        private static readonly ConcurrentBag<Action<Assembly>> _clearModActions = new();
        private static readonly ConcurrentBag<Action> _clearAllActions = new();

        public static void Register(Action<Assembly> clearMod, Action clearAll)
        {
            if (clearMod != null) _clearModActions.Add(clearMod);
            if (clearAll != null) _clearAllActions.Add(clearAll);
        }

        public static void ClearModHooks(Assembly modAssembly)
        {
            foreach (var action in _clearModActions)
            {
                try
                {
                    action(modAssembly);
                }
                catch
                {
                }
            }
        }

        public static void ClearAll()
        {
            foreach (var action in _clearAllActions)
            {
                try
                {
                    action();
                }
                catch
                {
                }
            }
        }
    }
}
