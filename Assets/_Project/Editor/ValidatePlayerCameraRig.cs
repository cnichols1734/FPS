using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaFps.EditorTools
{
    /// <summary>
    /// Capture and level-dressing passes reframe cameras by assigning world-space poses. When that
    /// lands on the gameplay camera while it is still parented under the pivot, Unity converts it
    /// into a large local offset and the next SaveScene bakes it in permanently — the view ends up
    /// on a boom arm metres from the collision capsule, which reads as broken look and collision.
    /// This clamps the rig back to the prefab pose on every scene save.
    /// </summary>
    [InitializeOnLoad]
    public static class ValidatePlayerCameraRig
    {
        const float PivotEyeHeight = 1.6f;
        const float PositionTolerance = 0.0001f;
        const float AngleTolerance = 0.05f;

        static ValidatePlayerCameraRig()
        {
            EditorSceneManager.sceneSaving += OnSceneSaving;
        }

        static void OnSceneSaving(Scene scene, string path) => Validate(scene, autoFix: true);

        [MenuItem("Arena FPS/Validate Player Camera Rig")]
        static void ValidateMenu()
        {
            var scene = SceneManager.GetActiveScene();
            if (!Validate(scene, autoFix: true))
                Debug.Log("[CameraRig] Player camera rig is correct.");
        }

        /// <summary>Returns true when something was out of spec.</summary>
        static bool Validate(Scene scene, bool autoFix)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return false;

            bool foundProblem = false;

            foreach (var root in scene.GetRootGameObjects())
            {
                var controller = root.GetComponentInChildren<ArenaFps.Player.FpsController>(true);
                if (controller == null)
                    continue;

                var pivot = controller.CameraPivot;
                if (pivot == null)
                {
                    Debug.LogError($"[CameraRig] '{controller.name}' has no CameraPivot assigned — look control will be mis-wired.", controller);
                    foundProblem = true;
                    continue;
                }

                foundProblem |= CheckPivot(pivot, autoFix);

                var cam = pivot.GetComponentInChildren<Camera>(true);
                if (cam != null && cam.transform != pivot)
                    foundProblem |= CheckCamera(cam.transform, autoFix);
            }

            return foundProblem;
        }

        static bool CheckPivot(Transform pivot, bool autoFix)
        {
            var expected = new Vector3(0f, PivotEyeHeight, 0f);

            // Eye height is animated at runtime for crouch, so only the horizontal drift is a defect.
            var local = pivot.localPosition;
            bool offAxis = Mathf.Abs(local.x) > 0.001f || Mathf.Abs(local.z) > 0.001f;
            bool rotated = Quaternion.Angle(pivot.localRotation, Quaternion.identity) > AngleTolerance;
            if (!offAxis && !rotated)
                return false;

            Debug.LogWarning($"[CameraRig] CameraPivot drifted (local pos {local}, rot {pivot.localEulerAngles}). Expected {expected} with identity rotation.", pivot);
            if (autoFix)
            {
                pivot.localPosition = new Vector3(0f, local.y, 0f);
                pivot.localRotation = Quaternion.identity;
            }

            return true;
        }

        static bool CheckCamera(Transform cam, bool autoFix)
        {
            if (cam.localPosition.sqrMagnitude <= PositionTolerance
                && Quaternion.Angle(cam.localRotation, Quaternion.identity) <= AngleTolerance)
                return false;

            Debug.LogWarning($"[CameraRig] '{cam.name}' is offset from the pivot (local pos {cam.localPosition}, " +
                             $"rot {cam.localEulerAngles}). This puts the view on a boom arm away from the collision " +
                             "capsule. Resetting to the pivot origin.", cam);
            if (autoFix)
            {
                cam.localPosition = Vector3.zero;
                cam.localRotation = Quaternion.identity;
                cam.localScale = Vector3.one;
            }

            return true;
        }
    }
}
