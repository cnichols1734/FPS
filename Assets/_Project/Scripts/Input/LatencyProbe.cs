using System.Diagnostics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ArenaFps.Input
{
    /// <summary>
    /// Measures click-to-callback latency for Fire. Pair with DevCapture muzzle flash
    /// frame strips for the full click-to-photon budget (&lt; 25 ms target).
    /// </summary>
    public sealed class LatencyProbe : MonoBehaviour
    {
        [SerializeField] bool logToConsole = true;

        readonly Stopwatch _sinceFire = new Stopwatch();
        double _lastFireInputMs;
        double _lastMuzzleMs;
        int _samples;
        double _sumClickToMuzzleMs;

        public double LastClickToMuzzleMs => _lastMuzzleMs;
        public double AverageClickToMuzzleMs => _samples > 0 ? _sumClickToMuzzleMs / _samples : 0;

        public void NotifyFireInput()
        {
            _sinceFire.Restart();
            _lastFireInputMs = Time.realtimeSinceStartupAsDouble * 1000.0;
        }

        public void NotifyMuzzleFlash()
        {
            if (!_sinceFire.IsRunning)
                return;

            _lastMuzzleMs = _sinceFire.Elapsed.TotalMilliseconds;
            _sinceFire.Reset();
            _samples++;
            _sumClickToMuzzleMs += _lastMuzzleMs;

            if (logToConsole)
            {
                UnityEngine.Debug.Log(
                    $"[LatencyProbe] click→muzzle {_lastMuzzleMs:F2} ms (avg {AverageClickToMuzzleMs:F2} over {_samples})");
            }
        }

        void Update()
        {
            // Fallback probe if GameInput is absent — Mouse left button edge.
            if (GameInput.Instance == null && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                NotifyFireInput();
        }
    }
}
