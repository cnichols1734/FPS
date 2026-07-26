using ArenaFps.Audio;
using UnityEngine;

namespace ArenaFps.Player
{
    /// <summary>
    /// The player's own footsteps, played flat rather than positionally — your boots are not a point
    /// in the world you are listening to. Clips come from the baked bank, so a sprint across the
    /// arena does not allocate a clip per stride.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class FootstepAudio : MonoBehaviour
    {
        [SerializeField] float stepInterval = 0.42f;
        [SerializeField] float sprintInterval = 0.32f;
        [SerializeField] float crouchInterval = 0.62f;
        [SerializeField] float volume = 0.26f;

        CharacterController _cc;
        FpsController _fps;
        float _timer;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _fps = GetComponent<FpsController>();
        }

        void Update()
        {
            if (!_cc.isGrounded)
            {
                _timer = 0f;
                return;
            }

            var horizontal = new Vector3(_cc.velocity.x, 0f, _cc.velocity.z);
            float speed = horizontal.magnitude;
            if (speed < 0.8f)
            {
                _timer = 0f;
                return;
            }

            bool sprint = _fps != null && _fps.IsSprinting;
            bool crouch = _fps != null && _fps.IsCrouching;
            if (_fps != null && _fps.IsSliding)
            {
                _timer = 0f;
                return;
            }

            float interval = sprint ? sprintInterval : crouch ? crouchInterval : stepInterval;
            _timer += Time.deltaTime;
            if (_timer < interval)
                return;

            _timer = 0f;
            Sfx3D.Instance.Play2D(
                sprint ? Sfx.FootstepSprint : Sfx.Footstep,
                volume * (crouch ? 0.45f : 1f),
                0.07f);
        }
    }
}
