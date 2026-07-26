using ArenaFps.Player;
using UnityEngine;

namespace ArenaFps.Weapons
{
    /// <summary>
    /// Safety net for older Player prefabs that wake before WeaponController can build the rifle.
    /// </summary>
    public sealed class ViewmodelBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Ensure()
        {
            var fps = Object.FindAnyObjectByType<FpsController>();
            if (fps == null)
                return;

            var weapon = fps.GetComponent<WeaponController>();
            if (weapon == null)
                return;

            var weaponRoot = fps.transform.Find("CameraPivot/WeaponRoot")
                             ?? fps.transform.Find("WeaponRoot");
            if (weaponRoot == null)
                return;

            // WeaponController normally builds this in Awake. Rebuilding would throw away a solved
            // pose and a live playable graph, so only step in when nothing got built.
            var gun = weaponRoot.Find(ScarHViewmodelBuilder.RootName);
            if (gun != null)
                return;

            gun = ScarHViewmodelBuilder.Ensure(weaponRoot);
            var motion = weaponRoot.GetComponent<ViewmodelMotion>()
                         ?? weaponRoot.gameObject.AddComponent<ViewmodelMotion>();
            motion.Bind(fps);
            motion.ConfigureAuthoredFpsPose();

            var sight = ScarHViewmodelBuilder.FindDeep(gun, "SightAlign");
            if (sight != null && fps.CameraPivot != null)
                motion.ConfigureIronSightAds(sight, fps.CameraPivot);

            weapon.RefreshViewmodelAnchors();

            if (fps.GetComponent<AdsDepthOfField>() == null)
                fps.gameObject.AddComponent<AdsDepthOfField>();
        }
    }
}
