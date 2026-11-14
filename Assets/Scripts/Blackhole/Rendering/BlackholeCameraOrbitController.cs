using UnityEngine;
using UnityEngine.InputSystem;
using OmniTwin.BlackholeSimulation.Systems;

namespace OmniTwin.BlackholeSimulation.Rendering
{
    /// <summary>🎥 Smooth desktop and browser camera orbit around the simulated black hole.</summary>
    [DisallowMultipleComponent]
    public sealed class BlackholeCameraOrbitController : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField, Min(0.1f)] private float orbitSpeed = 0.18f;
        [SerializeField, Min(0.1f)] private float zoomSpeed = 2.5f;
        [SerializeField, Min(0.01f)] private float smoothing = 8f;
        [SerializeField] private float minimumDistance = 10f;
        [SerializeField] private float maximumDistance = 32f;

        private float _azimuth;
        private float _elevation;
        private float _distance;
        private float _targetAzimuth;
        private float _targetElevation;
        private float _targetDistance;

        public void SetTarget(Transform value) => target = value;

        private void OnEnable()
        {
            if (target == null)
                return;

            Vector3 offset = transform.position - target.position;
            _distance = _targetDistance = Mathf.Clamp(offset.magnitude, minimumDistance, maximumDistance);
            _azimuth = _targetAzimuth = Mathf.Atan2(offset.x, -offset.z) * Mathf.Rad2Deg;
            _elevation = _targetElevation = Mathf.Asin(Mathf.Clamp(offset.y / Mathf.Max(offset.magnitude, 1e-3f), -1f, 1f)) * Mathf.Rad2Deg;
        }

        private void LateUpdate()
        {
            if (target == null || BlackholeInteractionState.DeterministicMode)
                return;

            float dt = Time.unscaledDeltaTime;
            Mouse mouse = Mouse.current;
            Keyboard keyboard = Keyboard.current;
            Vector2 drag = mouse != null && mouse.rightButton.isPressed ? mouse.delta.ReadValue() : Vector2.zero;
            float horizontal = Axis(keyboard, Key.D, Key.RightArrow) - Axis(keyboard, Key.A, Key.LeftArrow);
            float vertical = Axis(keyboard, Key.W, Key.UpArrow) - Axis(keyboard, Key.S, Key.DownArrow);
            _targetAzimuth += horizontal * 70f * orbitSpeed * dt + drag.x * 0.25f * orbitSpeed;
            _targetElevation = Mathf.Clamp(_targetElevation + vertical * 55f * orbitSpeed * dt - drag.y * 0.20f * orbitSpeed, 4f, 68f);
            if (mouse != null)
                _targetDistance = Mathf.Clamp(_targetDistance - mouse.scroll.ReadValue().y * zoomSpeed * 0.01f, minimumDistance, maximumDistance);

            float blend = 1f - Mathf.Exp(-smoothing * dt);
            _azimuth = Mathf.LerpAngle(_azimuth, _targetAzimuth, blend);
            _elevation = Mathf.Lerp(_elevation, _targetElevation, blend);
            _distance = Mathf.Lerp(_distance, _targetDistance, blend);
            float elevationRadians = _elevation * Mathf.Deg2Rad;
            float azimuthRadians = _azimuth * Mathf.Deg2Rad;
            Vector3 offset = new Vector3(
                Mathf.Sin(azimuthRadians) * Mathf.Cos(elevationRadians),
                Mathf.Sin(elevationRadians),
                -Mathf.Cos(azimuthRadians) * Mathf.Cos(elevationRadians)) * _distance;
            transform.position = target.position + offset;
            transform.rotation = Quaternion.LookRotation(target.position - transform.position, Vector3.up);
        }

        private static float Axis(Keyboard keyboard, Key primary, Key alternate)
        {
            if (keyboard == null)
                return 0f;
            return (keyboard[primary].isPressed || keyboard[alternate].isPressed) ? 1f : 0f;
        }
    }
}
