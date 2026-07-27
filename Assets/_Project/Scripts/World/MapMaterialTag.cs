using UnityEngine;

namespace ArenaFps.World
{
    /// <summary>Marks geometry with a material key the ArenaDresser resolves at runtime.</summary>
    public sealed class MapMaterialTag : MonoBehaviour
    {
        public string materialKey = "Mat_Concrete";
    }
}
