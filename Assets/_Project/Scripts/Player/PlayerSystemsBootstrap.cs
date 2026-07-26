using ArenaFps.Combat;
using ArenaFps.Core;
using ArenaFps.UI;
using UnityEngine;

namespace ArenaFps.Player
{
    /// <summary>
    /// Guarantees the player's runtime components regardless of how old the prefab is. The shipped
    /// prefab predates health, HUD and screen feedback, and re-authoring it by hand would silently
    /// break anyone's local scene — attaching here is the durable fix.
    /// </summary>
    public sealed class PlayerSystemsBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Ensure()
        {
            var fps = FindAnyObjectByType<FpsController>();
            if (fps == null)
                return;

            var go = fps.gameObject;
            go.layer = GameLayers.Player;

            var health = go.GetComponent<Damageable>();
            if (health == null)
                health = go.AddComponent<Damageable>();
            health.MarkAsPlayer();
            health.ConfigureMaxHealth(100f);

            if (go.GetComponent<PlayerHealth>() == null)
                go.AddComponent<PlayerHealth>();
            if (go.GetComponent<FootstepAudio>() == null)
                go.AddComponent<FootstepAudio>();
            if (go.GetComponent<ScreenLook>() == null)
                go.AddComponent<ScreenLook>();
            if (go.GetComponent<PlayerCombatFeedback>() == null)
                go.AddComponent<PlayerCombatFeedback>();
            if (go.GetComponent<HudView>() == null)
                go.AddComponent<HudView>();
            if (go.GetComponent<AimAssist>() == null)
                go.AddComponent<AimAssist>();
        }
    }
}
