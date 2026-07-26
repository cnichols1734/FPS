using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ArenaFps.AI;
using ArenaFps.Audio;
using ArenaFps.Combat;
using ArenaFps.Core;
using ArenaFps.Feedback;
using ArenaFps.Player;
using UnityEngine;

namespace ArenaFps.DevTools
{
    /// <summary>
    /// Drives a scripted engagement and checks that a hit actually produces impact: limb hitboxes
    /// resolve, the struck bone flinches, blood and decals spawn, and death hands the pose to
    /// physics. Written because "the bots take damage" and "the bots feel hit" are different claims
    /// and only the second one matters. Run via Arena FPS → Run Combat Self Test.
    /// </summary>
    public sealed class CombatSelfTest : MonoBehaviour
    {
        [SerializeField] string relativeOutDir = "Tools/VisualQA/out";
        [SerializeField] float botDistance = 7f;
        [SerializeField] bool captureFrames = true;

        readonly List<string> _lines = new();
        int _passed;
        int _failed;

        IEnumerator Start()
        {
            _lines.Add($"# Combat self test — {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _lines.Add($"Unity {Application.unityVersion}");
            _lines.Add("");

            yield return new WaitForSeconds(0.75f);

            var player = FindAnyObjectByType<FpsController>();
            Check("player present", player != null);
            if (player == null)
            {
                Finish();
                yield break;
            }

            var camera = player.GetComponentInChildren<Camera>();
            Check("player camera present", camera != null);
            Check("camera tagged MainCamera (particles billboard to it)", Camera.main != null);
            Check("player has Damageable", player.GetComponent<Damageable>() != null);
            Check("audio bank baked", SfxBank.Get(Sfx.RifleShot) != null);
            Check("impact fx alive", ImpactFx.Instance != null);

            // Freeze the ambient encounter so the measurements below are about one bot, not six.
            foreach (var brain in FindObjectsByType<BotBrain>())
                brain.enabled = false;
            foreach (var weapon in FindObjectsByType<BotWeapon>())
                weapon.enabled = false;

            yield return TestBot(camera != null ? camera.transform : player.transform);

            Finish();
        }

        IEnumerator TestBot(Transform view)
        {
            var spawn = PlaceTarget(view);
            var facing = -view.forward;
            facing.y = 0f;

            var bot = BotFactory.Create(spawn, Quaternion.LookRotation(facing.normalized));
            bot.name = "Bot_SelfTest";
            bot.GetComponent<BotBrain>().enabled = false;
            bot.GetComponent<BotWeapon>().enabled = false;

            var rig = bot.GetComponent<BotRig>();
            var health = bot.GetComponent<Damageable>();
            var ragdoll = bot.GetComponent<RagdollDriver>();

            Check("rig component present", rig != null);
            Check("ragdoll driver present", ragdoll != null);
            if (rig == null || health == null || ragdoll == null)
                yield break;

            int bones = 0, hitboxes = 0, onHitboxLayer = 0, kinematic = 0;
            foreach (var bone in rig.Bones)
            {
                if (bone?.Transform == null)
                    continue;
                bones++;
                if (bone.Hitbox != null) hitboxes++;
                if (bone.Transform.gameObject.layer == GameLayers.Hitbox) onHitboxLayer++;
                if (bone.Body != null && bone.Body.isKinematic) kinematic++;
            }

            Check($"all 12 bones built (got {bones})", bones == (int)Bone.Count);
            Check($"every bone has a hitbox (got {hitboxes})", hitboxes == bones);
            Check($"every bone on Hitbox layer (got {onHitboxLayer})", onHitboxLayer == bones);
            Check($"bones kinematic while alive (got {kinematic})", kinematic == bones);
            Check("head hitbox multiplier > 1", rig.Head?.Hitbox != null && rig.Head.Hitbox.damageMultiplier > 1f);

            yield return null;

            // A bullet must be able to reach a limb: this is the check that would have caught the
            // bot-blinding layer mask bug from the other direction.
            var chest = rig[Bone.Chest];
            var toChest = BotRig.Center(chest) - view.position;
            bool traced = Physics.Raycast(view.position, toChest.normalized, out var hit, toChest.magnitude + 1f,
                GameLayers.PlayerBulletMask, QueryTriggerInteraction.Ignore);
            Check("player bullet mask reaches a limb collider", traced && hit.collider.GetComponent<Hitbox>() != null);

            // --- flinch on a survivable hit
            int particlesBefore = ImpactFx.Instance.LiveParticles;
            int decalsBefore = ImpactFx.Instance.LiveDecals;

            var arm = rig[Bone.UpperArmR];
            health.ApplyDamage(new DamageInfo
            {
                Amount = 18f,
                Point = BotRig.Center(arm),
                Direction = view.forward,
                Normal = -view.forward,
                Collider = arm.Collider,
                FromPlayer = true,
                Multiplier = 1f,
            });

            Check("survivable hit did not kill", !health.IsDead);
            Check("struck bone gained punch velocity", arm.PunchVelocity.sqrMagnitude > 0.01f);
            Check($"blood particles spawned (+{ImpactFx.Instance.LiveParticles - particlesBefore})",
                ImpactFx.Instance.LiveParticles > particlesBefore);
            Check($"blood decal placed (+{ImpactFx.Instance.LiveDecals - decalsBefore})",
                ImpactFx.Instance.LiveDecals > decalsBefore);

            yield return new WaitForSeconds(0.2f);
            Check("punch springs back toward the pose", arm.PunchAngles.sqrMagnitude > 0f);

            if (captureFrames)
                yield return Capture("selftest_flinch", 3);

            // --- lethal headshot
            var head = rig[Bone.Head];
            health.ApplyDamage(new DamageInfo
            {
                Amount = 500f,
                Point = BotRig.Center(head),
                Direction = view.forward,
                Normal = -view.forward,
                Collider = head.Collider,
                FromPlayer = true,
                Multiplier = 1f,
            });

            Check("lethal hit killed", health.IsDead);
            Check("ragdoll activated", ragdoll.IsRagdolled);

            yield return new WaitForFixedUpdate();

            int dynamic = 0, joints = 0;
            foreach (var bone in rig.Bones)
            {
                if (bone?.Body == null)
                    continue;
                if (!bone.Body.isKinematic) dynamic++;
                if (bone.Transform.GetComponent<CharacterJoint>() != null) joints++;
            }
            Check($"bones handed to physics (got {dynamic})", dynamic == bones);
            Check($"joints created on non-root bones (got {joints})", joints == bones - 1);
            Check("navmesh agent released", !bot.GetComponent<UnityEngine.AI.NavMeshAgent>().enabled);

            var restPosition = head.Transform.position;
            yield return new WaitForSeconds(0.6f);
            Check("body actually moves under gravity",
                (head.Transform.position - restPosition).sqrMagnitude > 0.0004f);

            if (captureFrames)
                yield return Capture("selftest_ragdoll", 4);
        }

        /// <summary>
        /// Puts the target in clear line of sight on solid floor. Spawning blind at a fixed offset
        /// buries it in whatever wall the player happens to be facing, and then every later check
        /// fails for the wrong reason.
        /// </summary>
        Vector3 PlaceTarget(Transform view)
        {
            var forward = view.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;

            float distance = botDistance;
            if (Physics.Raycast(view.position, forward, out var wall, botDistance + 1.5f,
                    GameLayers.WorldMask, QueryTriggerInteraction.Ignore))
                distance = Mathf.Max(3f, wall.distance - 1.5f);

            var spawn = view.position + forward * distance;
            return Physics.Raycast(spawn + Vector3.up * 2f, Vector3.down, out var floor, 12f,
                GameLayers.WorldMask, QueryTriggerInteraction.Ignore)
                ? floor.point
                : new Vector3(spawn.x, view.position.y - 1.6f, spawn.z);
        }

        IEnumerator Capture(string label, int frames)
        {
            string root = OutPath(label);
            Directory.CreateDirectory(root);
            for (int i = 0; i < frames; i++)
            {
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot(Path.Combine(root, $"frame_{i:00}.png"));
            }
            _lines.Add($"- captured `{label}` ({frames} frames)");
        }

        void Check(string what, bool ok)
        {
            if (ok) _passed++; else _failed++;
            _lines.Add($"{(ok ? "PASS" : "FAIL")}  {what}");
            if (!ok)
                Debug.LogError($"[CombatSelfTest] FAIL {what}");
        }

        void Finish()
        {
            _lines.Add("");
            _lines.Add($"{_passed} passed, {_failed} failed");

            string path = Path.Combine(OutPath(string.Empty), "combat-selftest.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, string.Join("\n", _lines), Encoding.UTF8);

            if (_failed == 0)
                Debug.Log($"[CombatSelfTest] {_passed} passed → {path}");
            else
                Debug.LogError($"[CombatSelfTest] {_failed} FAILED, {_passed} passed → {path}");
        }

        string OutPath(string label) =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", relativeOutDir, label));
    }
}
