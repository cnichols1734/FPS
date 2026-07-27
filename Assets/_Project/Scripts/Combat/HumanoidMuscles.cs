using System.Collections.Generic;
using UnityEngine;

namespace ArenaFps.Combat
{
    /// <summary>
    /// Named access to Mecanim muscle slots. <see cref="HumanTrait.MuscleName"/> is a linear array
    /// searched by string; resolving names per bot per frame would dominate the animation budget,
    /// so indices are looked up once into a shared table.
    /// </summary>
    public static class HumanoidMuscles
    {
        public const int Missing = -1;

        static Dictionary<string, int> _indices;

        public static int Index(string muscleName)
        {
            if (_indices == null)
            {
                var names = HumanTrait.MuscleName;
                _indices = new Dictionary<string, int>(names.Length);
                for (int i = 0; i < names.Length; i++)
                    _indices[names[i]] = i;
            }

            return _indices.TryGetValue(muscleName, out int index) ? index : Missing;
        }

        /// <summary>
        /// Writes a muscle if the avatar actually has it. Optional bones (UpperChest, Neck, Toes)
        /// are absent on many rigs, and a blind index write would throw or corrupt a neighbour.
        /// </summary>
        public static void Set(float[] muscles, int index, float value)
        {
            if (muscles == null || index < 0 || index >= muscles.Length)
                return;
            muscles[index] = Mathf.Clamp(value, -1f, 1f);
        }

        public static void Add(float[] muscles, int index, float value)
        {
            if (muscles == null || index < 0 || index >= muscles.Length)
                return;
            muscles[index] = Mathf.Clamp(muscles[index] + value, -1f, 1f);
        }
    }
}
