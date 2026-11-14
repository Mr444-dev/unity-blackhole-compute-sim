using UnityEngine;
using UnityEngine.InputSystem;
using OmniTwin.BlackholeSimulation.Rendering;

namespace OmniTwin.BlackholeSimulation.Systems
{
    /// <summary>🌟 Launches a compact stellar stream and presents its horizon-capture flare.</summary>
    [DisallowMultipleComponent]
    public sealed class BlackholeTidalDisruptionController : MonoBehaviour
    {
        private const float CooldownSeconds = 10f;
        private static readonly int IntensityId = Shader.PropertyToID("_Intensity");

        [SerializeField] private BlackholeSimulationController simulation;
        [SerializeField] private Camera renderCamera;
        [SerializeField] private Material jetMaterial;
        [SerializeField, Min(0.1f)] private float flareDuration = 1.6f;

        private float _cooldownRemaining;
        private float _flareElapsed;
        private int _lastCaptureSequence;
        private Transform _positiveJet;
        private Transform _negativeJet;
        private MaterialPropertyBlock _jetProperties;

        public bool CanLaunch => simulation != null && simulation.isActiveAndEnabled && _cooldownRemaining <= 0f;
        public float CooldownRemaining => _cooldownRemaining;
        public bool IsSpaghettificationActive => simulation != null && simulation.TdeStreamActive;

        public void SetReferences(BlackholeSimulationController target, Camera camera, Material material)
        {
            simulation = target;
            renderCamera = camera;
            jetMaterial = material;
        }

        private void OnEnable()
        {
            if (simulation == null)
                simulation = GetComponent<BlackholeSimulationController>();
            _lastCaptureSequence = simulation != null ? simulation.TdeCaptureSequence : 0;
        }

        private void Update()
        {
            _cooldownRemaining = Mathf.Max(0f, _cooldownRemaining - Time.unscaledDeltaTime);
            UpdateCaptureFlare();
            if (BlackholeInteractionState.DeterministicMode || !CanLaunch)
                return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
            {
                LaunchAtDefaultPosition();
                return;
            }

            Mouse mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
                return;

            Vector2 pointer = mouse.position.ReadValue();
            float guiY = Screen.height - pointer.y;
            if (pointer.x <= 330f && guiY <= 190f)
                return;
            LaunchAtScreenPoint(pointer);
        }

        public bool LaunchFromHud() => !BlackholeInteractionState.DeterministicMode && LaunchAtDefaultPosition();

        public string GetHudStatus()
        {
            if (CanLaunch)
                return "[LMB / Space] Launch Star (TDE): READY";
            string activity = IsSpaghettificationActive ? " — Spaghettification active" : string.Empty;
            return $"[LMB / Space] Cooldown: {_cooldownRemaining:F1}s{activity}";
        }

        private bool LaunchAtDefaultPosition()
        {
            Vector3 direction = renderCamera != null ? renderCamera.transform.right : transform.right;
            Vector3 local = transform.InverseTransformDirection(direction);
            local.y = 0f;
            if (local.sqrMagnitude < 1e-5f)
                local = Vector3.forward;
            return LaunchAtWorldPosition(transform.TransformPoint(local.normalized * 8f));
        }

        private bool LaunchAtScreenPoint(Vector2 screenPoint)
        {
            if (renderCamera == null)
                return LaunchAtDefaultPosition();

            Ray ray = renderCamera.ScreenPointToRay(screenPoint);
            Plane diskPlane = new Plane(transform.up, transform.position);
            if (diskPlane.Raycast(ray, out float distance) && distance > 0f)
                return LaunchAtWorldPosition(ray.GetPoint(distance));
            return LaunchAtDefaultPosition();
        }

        private bool LaunchAtWorldPosition(Vector3 worldPosition)
        {
            if (!CanLaunch || !simulation.TryInjectTdeStar(ClampSpawnPosition(worldPosition), Random.Range(0, int.MaxValue)))
                return false;
            _cooldownRemaining = CooldownSeconds;
            return true;
        }

        private Vector3 ClampSpawnPosition(Vector3 worldPosition)
        {
            Vector3 local = transform.InverseTransformPoint(worldPosition);
            local.y = 0f;
            if (local.sqrMagnitude < 1e-5f)
                local = Vector3.forward;
            local = local.normalized * Mathf.Clamp(local.magnitude, 7f, 9f);
            return transform.TransformPoint(local);
        }

        private void UpdateCaptureFlare()
        {
            if (simulation != null && simulation.TdeCaptureSequence != _lastCaptureSequence)
            {
                _lastCaptureSequence = simulation.TdeCaptureSequence;
                _flareElapsed = 0f;
                EnsureJetObjects();
            }

            if (_positiveJet == null || _negativeJet == null)
                return;
            _flareElapsed += Time.unscaledDeltaTime;
            if (_flareElapsed >= flareDuration)
            {
                _positiveJet.gameObject.SetActive(false);
                _negativeJet.gameObject.SetActive(false);
                return;
            }

            float normalizedTime = Mathf.Clamp01(_flareElapsed / flareDuration);
            float envelope = Mathf.Sin(normalizedTime * Mathf.PI);
            float length = Mathf.Lerp(3f, 7f, envelope);
            float radius = Mathf.Lerp(0.08f, 0.30f, envelope);
            ConfigureJet(_positiveJet, 1f, length, radius, envelope * 7f);
            ConfigureJet(_negativeJet, -1f, length, radius, envelope * 7f);
        }

        private void EnsureJetObjects()
        {
            if (jetMaterial == null)
                return;
            _jetProperties ??= new MaterialPropertyBlock();
            if (_positiveJet != null && _negativeJet != null)
            {
                _positiveJet.gameObject.SetActive(true);
                _negativeJet.gameObject.SetActive(true);
                return;
            }
            _positiveJet = CreateJet("TDE Polar Jet +", 1f);
            _negativeJet = CreateJet("TDE Polar Jet -", -1f);
            _positiveJet.gameObject.SetActive(true);
            _negativeJet.gameObject.SetActive(true);
        }

        private Transform CreateJet(string objectName, float direction)
        {
            GameObject jet = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            jet.name = objectName;
            jet.transform.SetParent(transform, false);
            Collider collider = jet.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);
            MeshRenderer renderer = jet.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = jetMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            ConfigureJet(jet.transform, direction, 3f, 0.08f, 0f);
            return jet.transform;
        }

        private void ConfigureJet(Transform jet, float direction, float length, float radius, float intensity)
        {
            if (jet == null)
                return;
            jet.localPosition = Vector3.up * direction * length * 0.5f;
            jet.localRotation = Quaternion.identity;
            jet.localScale = new Vector3(radius, length * 0.5f, radius);
            if (jet.TryGetComponent(out MeshRenderer renderer))
            {
                renderer.GetPropertyBlock(_jetProperties);
                _jetProperties.SetFloat(IntensityId, intensity);
                renderer.SetPropertyBlock(_jetProperties);
            }
        }

        private void OnDestroy()
        {
            if (_positiveJet != null)
                Destroy(_positiveJet.gameObject);
            if (_negativeJet != null)
                Destroy(_negativeJet.gameObject);
        }
    }
}
