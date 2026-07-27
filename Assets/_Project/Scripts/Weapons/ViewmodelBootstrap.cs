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
            var gun = weaponRoot.Find(ScarHViewmodelBuilder.RootName)
                      ?? weaponRoot.Find(AcrViewmodelBuilder.RootName);
            if (gun != null)
                return;

            var motion = weaponRoot.GetComponent<ViewmodelMotion>()
                         ?? weaponRoot.gameObject.AddComponent<ViewmodelMotion>();
            motion.Bind(fps);
            // Seat against a zeroed WeaponRoot — same order as WeaponController.Awake.
            motion.ConfigureAuthoredFpsPose();

            gun = weapon.Slot == WeaponController.WeaponSlot.Carbine
                ? AcrViewmodelBuilder.Ensure(weaponRoot)
                : ScarHViewmodelBuilder.Ensure(weaponRoot);

            var sight = ScarHViewmodelBuilder.FindDeep(gun, "SightAlign")
                        ?? AcrViewmodelBuilder.FindDeep(gun, "SightAlign");
            if (sight != null && fps.CameraPivot != null)
            {
                if (weapon.Slot == WeaponController.WeaponSlot.Carbine)
                {
                    motion.ConfigureIronSightAds(sight, fps.CameraPivot, 0.16f, 0f);
                    motion.CalibrateAdsToViewportCenter(fps.CameraPivot, () =>
                        AcrViewmodelBuilder.TryMeasureReticleWorld(gun, out var point)
                            ? point
                            : sight.position);
                    motion.ConfigureAuthoredHipFraming(
                        gun, AcrViewmodelBuilder.HipPocket, AcrViewmodelBuilder.HipZoomScale);
                }
                else
                {
                    motion.ConfigureIronSightAds(sight, fps.CameraPivot);
                }
            }

            weapon.RefreshViewmodelAnchors();

            if (fps.GetComponent<AdsDepthOfField>() == null)
                fps.gameObject.AddComponent<AdsDepthOfField>();
        }
    }
}
