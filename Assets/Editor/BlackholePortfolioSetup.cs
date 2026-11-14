using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using OmniTwin.BlackholeSimulation.Benchmarking;
using OmniTwin.BlackholeSimulation.Core;
using OmniTwin.BlackholeSimulation.Rendering;
using OmniTwin.BlackholeSimulation.Systems;
using OmniTwin.BlackholeSimulation.UI;

namespace OmniTwin.BlackholeSimulation.Editor
{
    /// <summary>🧰 Wires the playable portfolio scene and runs EditMode checks plus the hardware benchmark.</summary>
    public static class BlackholePortfolioSetup
    {
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";
        private const string DiskShaderPath = "Assets/Shaders/Blackhole/AccretionDiskProcedural.shader";
        private const string CpuFallbackShaderPath = "Assets/Shaders/Blackhole/AccretionDiskCpuFallback.shader";
        private const string BackgroundShaderPath = "Assets/Shaders/Blackhole/BlackholeStarfieldSkybox.shader";
        private const string GravityComputePath = "Assets/Shaders/Blackhole/BlackholeGravity.compute";
        private const string LensingShaderPath = "Assets/Shaders/Blackhole/BlackholeLensing.shader";
        private const string DiskMaterialPath = "Assets/Materials/AccretionDiskProcedural.mat";
        private const string CpuFallbackMaterialPath = "Assets/Materials/AccretionDiskCpuFallback.mat";
        private const string BackgroundMaterialPath = "Assets/Materials/BlackholeStarfieldSkybox.mat";
        private const string HorizonMaterialPath = "Assets/Materials/BlackholeHorizon.mat";
        private const string TdeJetShaderPath = "Assets/Shaders/Blackhole/BlackholeTdeJet.shader";
        private const string TdeJetMaterialPath = "Assets/Materials/BlackholeTdeJet.mat";
        private const string BlackholeObjectName = "Blackhole Simulation";
        private const string BenchmarkSessionKey = "OmniTwin.BlackholeSimulation.RunBenchmarkAfterPlay";
        private const string BenchmarkOutputFileName = "Blackhole-Benchmark-Results.md";
        private const string ScreenshotSessionKey = "OmniTwin.BlackholeSimulation.CapturePortfolioScreenshot";
        internal const string WebGLBuildPendingSessionKey = "OmniTwin.BlackholeSimulation.WebGLBuildPending";
        internal const string WebGLBuildStartedSessionKey = "OmniTwin.BlackholeSimulation.WebGLBuildStarted";
        private static TestRunnerApi _testRunnerApi;
        private static VerificationCallbacks _verificationCallbacks;
        private static Action<bool> _deferredTestCompletion;
        private static bool _deferredTestCompletionPassed;
        private static double _testCleanupWaitStartedAt;

        /// <summary>🎬 Builds the configured black-hole demo and saves it to the build-settings scene.</summary>
        [MenuItem("Tools/Blackhole/1. Wire & Save Demo SampleScene")]
        public static void WireAndSaveDemoScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("Exit Play Mode before wiring the black-hole demo scene.");
                return;
            }

            try
            {
                Scene scene = BuildAndSaveDemoScene();
                if (scene.IsValid())
                    Debug.Log("Black-hole demo scene wired and saved to " + ScenePath);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        /// <summary>🧪 Runs the assertion checks, starts the full PlayMode sweep, and exports hardware metrics.</summary>
        [MenuItem("Tools/Blackhole/2. Run EditMode Checks & Export Benchmark Markdown")]
        public static void RunChecksAndBenchmark()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("Exit Play Mode before starting the black-hole verification and benchmark.");
                return;
            }

            try
            {
                RunEditModeChecks(passed =>
                {
                    if (!passed)
                    {
                        SessionState.SetBool(BenchmarkSessionKey, false);
                        return;
                    }

                    try
                    {
                        Scene scene = BuildAndSaveDemoScene();
                        if (!scene.IsValid())
                        {
                            SessionState.SetBool(BenchmarkSessionKey, false);
                            return;
                        }

                        SessionState.SetBool(BenchmarkSessionKey, true);
                        BlackholePlayModeSettings.EnableRunInBackground();
                        EditorApplication.isPlaying = true;
                    }
                    catch (Exception exception)
                    {
                        SessionState.SetBool(BenchmarkSessionKey, false);
                        Debug.LogException(exception);
                    }
                });
            }
            catch (Exception exception)
            {
                SessionState.SetBool(BenchmarkSessionKey, false);
                Debug.LogException(exception);
            }
        }

        /// <summary>📸 Captures the configured 100k GPU scene as a clean 1920x1080 Game View PNG.</summary>
        [MenuItem("Tools/Blackhole/3. Capture 1080p Portfolio Screenshot")]
        public static void CapturePortfolioScreenshot()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("Exit Play Mode before starting the portfolio screenshot capture.");
                return;
            }

            try
            {
                Scene scene = BuildAndSaveDemoScene();
                if (!scene.IsValid())
                    return;

                BlackholePlayModeSettings.EnableRunInBackground();
                SessionState.SetBool(ScreenshotSessionKey, true);
                EditorApplication.isPlaying = true;
            }
            catch (Exception exception)
            {
                SessionState.SetBool(ScreenshotSessionKey, false);
                Debug.LogException(exception);
            }
        }

        /// <summary>🌐 Builds the CPU-mesh WebGL player with itch.io-compatible compressed output.</summary>
        [MenuItem("Tools/Blackhole/4. Build WebGL for itch.io")]
        public static void BuildWebGLForItchIo()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("Exit Play Mode before building the itch.io WebGL player.");
                return;
            }

            try
            {
                if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
                {
                    Debug.LogError("Web Build Support is not installed for this Unity Editor. Install the Web Build Support module, then run this menu item again.");
                    return;
                }

                Scene scene = BuildAndSaveDemoScene();
                if (!scene.IsValid())
                    return;

                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
                PlayerSettings.WebGL.decompressionFallback = true;
                PlayerSettings.defaultWebScreenWidth = 1280;
                PlayerSettings.defaultWebScreenHeight = 720;

                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
                {
                    SessionState.SetBool(WebGLBuildPendingSessionKey, true);
                    SessionState.SetFloat(WebGLBuildStartedSessionKey, (float)EditorApplication.timeSinceStartup);
                    if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL))
                    {
                        SessionState.SetBool(WebGLBuildPendingSessionKey, false);
                        Debug.LogError("Unity could not switch the active platform to WebGL. The browser build was not started.");
                        return;
                    }
                    Debug.Log("Switching the active platform to WebGL; Unity will resume the itch.io build after the target-specific scripts compile.");
                    return;
                }
                BuildWebGLPlayer();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        internal static void ContinueWebGLBuildAfterTargetSwitch()
        {
            try
            {
                BuildWebGLPlayer();
            }
            catch (Exception exception)
            {
                SessionState.SetBool(WebGLBuildPendingSessionKey, false);
                Debug.LogException(exception);
            }
        }

        private static void BuildWebGLPlayer()
        {
            var enabledScenes = new System.Collections.Generic.List<string>();
            foreach (EditorBuildSettingsScene buildScene in EditorBuildSettings.scenes)
            {
                if (buildScene.enabled && !string.IsNullOrEmpty(buildScene.path))
                    enabledScenes.Add(buildScene.path);
            }
            if (enabledScenes.Count == 0)
                throw new InvalidOperationException("Enable the Blackhole demo scene in Build Settings before building for itch.io.");

            string outputPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Builds", "WebGL-ItchIo"));
            Directory.CreateDirectory(outputPath);
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = enabledScenes.ToArray(),
                locationPathName = outputPath,
                target = BuildTarget.WebGL,
                options = BuildOptions.None
            });

            if (report.summary.result == BuildResult.Succeeded)
                Debug.Log($"itch.io WebGL build complete: {outputPath} ({report.summary.totalSize / (1024f * 1024f):F1} MiB).");
            else
                Debug.LogError($"itch.io WebGL build failed with {report.summary.result}. Check the Unity Console and confirm Web Build Support is installed.");
        }

        public static void RunEditModeChecks(Action<bool> completed)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Run the Blackhole EditMode checks after exiting Play Mode.");
            if (_testRunnerApi != null)
                throw new InvalidOperationException("The Blackhole EditMode test suite is already running.");

            _testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            _verificationCallbacks = new VerificationCallbacks(completed);
            _testRunnerApi.RegisterCallbacks(_verificationCallbacks);
            try
            {
                _testRunnerApi.Execute(new ExecutionSettings(new Filter
                {
                    testMode = TestMode.EditMode,
                    assemblyNames = new[] { "Blackhole.Tests.EditMode" }
                }));
            }
            catch
            {
                CleanupTestRunner();
                throw;
            }
        }

        private static void OnEditModeChecksFinished(ITestResultAdaptor result, Action<bool> completed)
        {
            CleanupTestRunner();
            bool passed = result != null && result.TestStatus == TestStatus.Passed && result.FailCount == 0 && result.PassCount > 0;
            if (passed)
                Debug.Log($"Blackhole Simulation EditMode checks passed ({result.PassCount} tests).");
            else
                Debug.LogError(result == null
                    ? "Blackhole Simulation EditMode checks did not return a test result."
                    : $"Blackhole Simulation EditMode checks failed: {result.FailCount} failed, {result.PassCount} passed, {result.SkipCount} skipped.");

            _deferredTestCompletion = completed;
            _deferredTestCompletionPassed = passed;
            _testCleanupWaitStartedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update -= WaitForTestRunnerCleanup;
            EditorApplication.update += WaitForTestRunnerCleanup;
        }

        private static void WaitForTestRunnerCleanup()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                return;

            // RunFinished is raised before Unity's post-build cleanup tasks. Allow those tasks to
            // restore the editor scene setup before requesting Play Mode for the benchmark.
            if (EditorApplication.timeSinceStartup - _testCleanupWaitStartedAt < 5.0)
                return;

            EditorApplication.update -= WaitForTestRunnerCleanup;
            Action<bool> completed = _deferredTestCompletion;
            bool passed = _deferredTestCompletionPassed;
            _deferredTestCompletion = null;
            _deferredTestCompletionPassed = false;
            completed?.Invoke(passed);
        }

        private static void CleanupTestRunner()
        {
            if (_testRunnerApi != null && _verificationCallbacks != null)
                _testRunnerApi.UnregisterCallbacks(_verificationCallbacks);
            if (_testRunnerApi != null)
                UnityEngine.Object.DestroyImmediate(_testRunnerApi);
            _testRunnerApi = null;
            _verificationCallbacks = null;
        }

        private sealed class VerificationCallbacks : ICallbacks
        {
            private readonly Action<bool> _completed;

            public VerificationCallbacks(Action<bool> completed) => _completed = completed;
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }
            public void RunFinished(ITestResultAdaptor result) => OnEditModeChecksFinished(result, _completed);
        }

        private static Scene BuildAndSaveDemoScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return default;
            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), ScenePath)))
                throw new FileNotFoundException("The build-settings sample scene was not found.", ScenePath);

            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Camera camera = ConfigureCamera();
            RenderSettings.skybox = LoadOrCreateMaterial(BackgroundMaterialPath, BackgroundShaderPath, "Blackhole starfield skybox");
            ConfigureLighting();
            ConfigureGlobalVolume();
            Material diskMaterial = LoadOrCreateMaterial(DiskMaterialPath, DiskShaderPath, "Accretion disk");
            Material cpuFallbackMaterial = LoadOrCreateMaterial(CpuFallbackMaterialPath, CpuFallbackShaderPath, "Accretion disk CPU fallback");
            Material horizonMaterial = LoadOrCreateHorizonMaterial();
            Material tdeJetMaterial = LoadOrCreateMaterial(TdeJetMaterialPath, TdeJetShaderPath, "Blackhole TDE polar jet");
            ComputeShader compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(GravityComputePath);
            Shader lensingShader = AssetDatabase.LoadAssetAtPath<Shader>(LensingShaderPath);
            if (compute == null || lensingShader == null)
                throw new InvalidOperationException("The black-hole compute or lensing shader could not be loaded from Assets/Shaders/Blackhole.");

            GameObject blackholeObject = GameObject.Find(BlackholeObjectName);
            if (blackholeObject == null)
                blackholeObject = new GameObject(BlackholeObjectName);
            blackholeObject.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            blackholeObject.transform.localScale = Vector3.one;

            AccretionDiskController disk = blackholeObject.GetComponent<AccretionDiskController>();
            if (disk == null)
                disk = blackholeObject.AddComponent<AccretionDiskController>();
            BlackholeSimulationController simulation = blackholeObject.GetComponent<BlackholeSimulationController>();
            if (simulation == null)
                simulation = blackholeObject.AddComponent<BlackholeSimulationController>();
            BlackholeBenchmarkRecorder benchmark = blackholeObject.GetComponent<BlackholeBenchmarkRecorder>();
            if (benchmark == null)
                benchmark = blackholeObject.AddComponent<BlackholeBenchmarkRecorder>();
            BlackholeTidalDisruptionController tidalDisruption = blackholeObject.GetComponent<BlackholeTidalDisruptionController>();
            if (tidalDisruption == null)
                tidalDisruption = blackholeObject.AddComponent<BlackholeTidalDisruptionController>();

            SetObjectReference(disk, "particleMaterial", diskMaterial);
            SetObjectReference(disk, "cpuMeshFallbackMaterial", cpuFallbackMaterial);
            SetFloat(disk, "particleSize", 0.04f);
            ConfigureSimulation(simulation, compute, camera);
            ConfigureBenchmark(benchmark, simulation);
            tidalDisruption.SetReferences(simulation, camera, tdeJetMaterial);
            EditorUtility.SetDirty(tidalDisruption);
            ConfigureLensingRendererFeature(lensingShader);
            ConfigureHorizon(blackholeObject.transform, horizonMaterial, 0.65f);

            BlackholeTelemetryHud hud = camera.GetComponent<BlackholeTelemetryHud>();
            if (hud == null)
                hud = camera.gameObject.AddComponent<BlackholeTelemetryHud>();
            hud.SetSimulation(simulation);
            hud.SetTidalDisruptionController(tidalDisruption);
            EditorUtility.SetDirty(hud);

            BlackholeCameraOrbitController orbit = camera.GetComponent<BlackholeCameraOrbitController>();
            if (orbit == null)
                orbit = camera.gameObject.AddComponent<BlackholeCameraOrbitController>();
            orbit.SetTarget(blackholeObject.transform);
            EditorUtility.SetDirty(orbit);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new IOException("Unity could not save " + ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return scene;
        }

        private static Camera ConfigureCamera()
        {
            GameObject cameraObject = GameObject.FindGameObjectWithTag("MainCamera");
            if (cameraObject == null)
                cameraObject = new GameObject("Main Camera");
            cameraObject.name = "Main Camera";
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, 3.2f, -18.5f);
            cameraObject.transform.LookAt(Vector3.zero, Vector3.up);

            Camera camera = cameraObject.GetComponent<Camera>();
            if (camera == null)
                camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.backgroundColor = new Color(0.003f, 0.004f, 0.012f, 1f);
            camera.fieldOfView = 42f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 120f;
            camera.allowHDR = true;
            camera.allowMSAA = true;

            UniversalAdditionalCameraData cameraData = cameraObject.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData == null)
                cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderPostProcessing = true;
            cameraData.SetRenderer(GetActiveRendererIndex());
            EditorUtility.SetDirty(camera);
            EditorUtility.SetDirty(cameraData);
            return camera;
        }

        private static void ConfigureLighting()
        {
            GameObject lightObject = GameObject.Find("Directional Light");
            if (lightObject == null)
                lightObject = new GameObject("Directional Light");
            Light light = lightObject.GetComponent<Light>();
            if (light == null)
                light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.62f, 0.74f, 1f);
            light.intensity = 1.2f;
            light.shadows = LightShadows.None;
            lightObject.transform.rotation = Quaternion.Euler(37f, -28f, 0f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.025f, 0.04f, 0.095f);
            RenderSettings.ambientEquatorColor = new Color(0.012f, 0.018f, 0.04f);
            RenderSettings.ambientGroundColor = new Color(0.003f, 0.004f, 0.012f);
            RenderSettings.ambientIntensity = 1f;
            EditorUtility.SetDirty(light);
            EditorUtility.SetDirty(lightObject);
        }

        private static void ConfigureGlobalVolume()
        {
            const string profilePath = "Assets/Settings/SampleSceneProfile.asset";
            VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
            if (profile == null)
                throw new InvalidOperationException("The scene Global Volume profile is missing at " + profilePath);

            Volume volume = UnityEngine.Object.FindAnyObjectByType<Volume>();
            if (volume == null)
            {
                GameObject volumeObject = new GameObject("Global Volume");
                volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = true;
            }
            volume.sharedProfile = profile;
            if (!profile.TryGet(out Bloom bloom))
                bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(0.9f);
            bloom.intensity.Override(1.25f);
            bloom.scatter.Override(0.68f);
            if (!profile.TryGet(out Tonemapping tonemapping))
                tonemapping = profile.Add<Tonemapping>(true);
            tonemapping.mode.Override(TonemappingMode.ACES);
            EditorUtility.SetDirty(volume);
            EditorUtility.SetDirty(profile);
        }

        private static void ConfigureSimulation(BlackholeSimulationController simulation, ComputeShader compute, Camera camera)
        {
            SerializedObject serialized = new SerializedObject(simulation);
            SetObjectReference(serialized, "gravityCompute", compute);
            SetObjectReference(serialized, "renderCamera", camera);
            SerializedProperty backend = serialized.FindProperty("backend");
            if (backend == null)
                throw new MissingFieldException(nameof(BlackholeSimulationController), "backend");
            backend.enumValueIndex = (int)SimulationBackend.GPUCompute;
            SerializedProperty population = serialized.FindProperty("populationPreset");
            if (population != null)
                population.intValue = (int)ParticleCountPreset.OneHundredThousand;
            SerializedProperty settings = serialized.FindProperty("settings");
            if (settings != null)
            {
                settings.FindPropertyRelative("particleCount").intValue = (int)ParticleCountPreset.OneHundredThousand;
                settings.FindPropertyRelative("gravitationalParameter").floatValue = 1f;
                settings.FindPropertyRelative("schwarzschildRadius").floatValue = 0.65f;
                settings.FindPropertyRelative("outerRadius").floatValue = 11f;
                settings.FindPropertyRelative("diskHalfThickness").floatValue = 0.10f;
                settings.FindPropertyRelative("eccentricity").floatValue = 0.02f;
                settings.FindPropertyRelative("seed").intValue = 1847;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(simulation);
        }

        private static void ConfigureBenchmark(BlackholeBenchmarkRecorder benchmark, BlackholeSimulationController simulation)
        {
            SerializedObject serialized = new SerializedObject(benchmark);
            SetObjectReference(serialized, "simulation", simulation);
            serialized.FindProperty("autoRunOnStart").boolValue = false;
            serialized.FindProperty("outputFileName").stringValue = BenchmarkOutputFileName;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(benchmark);
        }

        private static void ConfigureLensingRendererFeature(Shader lensingShader)
        {
            ScriptableRendererData rendererData = GetActiveRendererData();
            if (rendererData == null)
                throw new InvalidOperationException("No URP renderer data is assigned to the active render pipeline.");

            if (!rendererData.TryGetRendererFeature(out BlackholeLensingFeature feature))
            {
                feature = ScriptableObject.CreateInstance<BlackholeLensingFeature>();
                feature.name = nameof(BlackholeLensingFeature);
                feature.SetActive(true);
                AssetDatabase.AddObjectToAsset(feature, rendererData);
                int featureIndex = rendererData.rendererFeatures.Count;
                rendererData.rendererFeatures.Add(feature);

                var serializedRenderer = new SerializedObject(rendererData);
                SerializedProperty featureMap = serializedRenderer.FindProperty("m_RendererFeatureMap");
                featureMap.arraySize = rendererData.rendererFeatures.Count;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId))
                    throw new InvalidOperationException("Unity could not create a local asset id for the lensing renderer feature.");
                featureMap.GetArrayElementAtIndex(featureIndex).longValue = localId;
                serializedRenderer.ApplyModifiedPropertiesWithoutUndo();
            }

            SerializedObject serializedFeature = new SerializedObject(feature);
            SetObjectReference(serializedFeature, "lensingShader", lensingShader);
            serializedFeature.FindProperty("lensingStrength").floatValue = 1.15f;
            serializedFeature.FindProperty("photonRingIntensity").floatValue = 2.1f;
            serializedFeature.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(feature);
            rendererData.SetDirty();
            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssets();
        }

        private static void ConfigureHorizon(Transform parent, Material material, float schwarzschildRadius)
        {
            Transform existing = parent.Find("Black Hole Event Horizon");
            GameObject horizon;
            if (existing == null)
            {
                horizon = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                horizon.name = "Black Hole Event Horizon";
                horizon.transform.SetParent(parent, false);
                Collider collider = horizon.GetComponent<Collider>();
                if (collider != null)
                    UnityEngine.Object.DestroyImmediate(collider);
            }
            else
            {
                horizon = existing.gameObject;
            }

            horizon.transform.localPosition = Vector3.zero;
            horizon.transform.localRotation = Quaternion.identity;
            horizon.transform.localScale = Vector3.one * (2f * schwarzschildRadius);
            MeshRenderer renderer = horizon.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            EditorUtility.SetDirty(horizon);
        }

        private static Material LoadOrCreateMaterial(string materialPath, string shaderPath, string materialName)
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (shader == null)
                throw new InvalidOperationException("Required shader not found at " + shaderPath);

            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                EnsureMaterialsFolder();
                material = new Material(shader) { name = materialName };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            else
            {
                material.shader = shader;
                EditorUtility.SetDirty(material);
            }
            return material;
        }

        private static Material LoadOrCreateHorizonMaterial()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(HorizonMaterialPath);
            if (material != null)
                return material;

            EnsureMaterialsFolder();
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                throw new InvalidOperationException("Could not locate an unlit shader for the black-hole horizon mesh.");

            material = new Material(shader) { name = "Blackhole Horizon" };
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", Color.black);
            else if (material.HasProperty("_Color"))
                material.SetColor("_Color", Color.black);
            AssetDatabase.CreateAsset(material, HorizonMaterialPath);
            return material;
        }

        private static void EnsureMaterialsFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
                AssetDatabase.CreateFolder("Assets", "Materials");
        }

        private static ScriptableRendererData GetActiveRendererData()
        {
            UniversalRenderPipelineAsset pipeline = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
                pipeline = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
                pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
            if (pipeline == null)
                return AssetDatabase.LoadAssetAtPath<ScriptableRendererData>("Assets/Settings/PC_Renderer.asset");

            SerializedObject serialized = new SerializedObject(pipeline);
            SerializedProperty rendererDataList = serialized.FindProperty("m_RendererDataList");
            SerializedProperty defaultRenderer = serialized.FindProperty("m_DefaultRendererIndex");
            if (rendererDataList == null || rendererDataList.arraySize == 0)
                return null;

            int index = defaultRenderer != null ? defaultRenderer.intValue : 0;
            index = Mathf.Clamp(index, 0, rendererDataList.arraySize - 1);
            ScriptableRendererData rendererData = rendererDataList.GetArrayElementAtIndex(index).objectReferenceValue as ScriptableRendererData;
            return rendererData != null
                ? rendererData
                : AssetDatabase.LoadAssetAtPath<ScriptableRendererData>("Assets/Settings/PC_Renderer.asset");
        }

        private static int GetActiveRendererIndex()
        {
            UniversalRenderPipelineAsset pipeline = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
                pipeline = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
                return 0;

            SerializedProperty defaultRenderer = new SerializedObject(pipeline).FindProperty("m_DefaultRendererIndex");
            return defaultRenderer != null ? Mathf.Max(0, defaultRenderer.intValue) : 0;
        }

        private static void SetObjectReference(UnityEngine.Object target, string propertyName, UnityEngine.Object value)
        {
            SerializedObject serialized = new SerializedObject(target);
            SetObjectReference(serialized, propertyName, value);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
        }

        private static void SetObjectReference(SerializedObject serialized, string propertyName, UnityEngine.Object value)
        {
            serialized.Update();
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null)
                throw new MissingFieldException(serialized.targetObject.GetType().Name, propertyName);
            property.objectReferenceValue = value;
        }

        private static void SetFloat(UnityEngine.Object target, string propertyName, float value)
        {
            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null)
                throw new MissingFieldException(target.GetType().Name, propertyName);
            property.floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
        }
    }

    /// <summary>🌱 Keeps Editor Play Mode advancing during capture/benchmark, then restores the prior Player setting.</summary>
    [InitializeOnLoad]
    internal static class BlackholePlayModeSettings
    {
        private const string HasOriginalValueKey = "OmniTwin.BlackholeSimulation.HasOriginalRunInBackground";
        private const string OriginalValueKey = "OmniTwin.BlackholeSimulation.OriginalRunInBackground";

        static BlackholePlayModeSettings()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        internal static void EnableRunInBackground()
        {
            if (!SessionState.GetBool(HasOriginalValueKey, false))
            {
                SessionState.SetBool(OriginalValueKey, Application.runInBackground);
                SessionState.SetBool(HasOriginalValueKey, true);
            }
            Application.runInBackground = true;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode || !SessionState.GetBool(HasOriginalValueKey, false))
                return;

            Application.runInBackground = SessionState.GetBool(OriginalValueKey, false);
            SessionState.EraseBool(OriginalValueKey);
            SessionState.EraseBool(HasOriginalValueKey);
        }
    }

    /// <summary>🌐 Resumes an itch.io build after Unity reloads scripts for the WebGL target.</summary>
    [InitializeOnLoad]
    internal static class BlackholeWebGLBuildRunner
    {
        private const double TargetSwitchTimeoutSeconds = 90.0;

        static BlackholeWebGLBuildRunner()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (!SessionState.GetBool(BlackholePortfolioSetup.WebGLBuildPendingSessionKey, false))
                return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
            {
                double startedAt = SessionState.GetFloat(BlackholePortfolioSetup.WebGLBuildStartedSessionKey, (float)EditorApplication.timeSinceStartup);
                if (EditorApplication.timeSinceStartup - startedAt <= TargetSwitchTimeoutSeconds)
                    return;

                SessionState.SetBool(BlackholePortfolioSetup.WebGLBuildPendingSessionKey, false);
                Debug.LogError("Unity did not finish switching the active target to WebGL, so the itch.io build was cancelled.");
                return;
            }

            SessionState.SetBool(BlackholePortfolioSetup.WebGLBuildPendingSessionKey, false);
            EditorApplication.delayCall += BlackholePortfolioSetup.ContinueWebGLBuildAfterTargetSwitch;
        }
    }

    /// <summary>⏱️ Continues the requested benchmark after Unity's EditMode-to-PlayMode domain reload.</summary>
    [InitializeOnLoad]
    internal static class BlackholeBenchmarkEditorRunner
    {
        private const string SessionKey = "OmniTwin.BlackholeSimulation.RunBenchmarkAfterPlay";
        private const string OutputFileName = "Blackhole-Benchmark-Results.md";
        private static BlackholeBenchmarkRecorder _recorder;
        private static double _waitStartedAt;
        private static bool _monitoringBenchmark;

        static BlackholeBenchmarkEditorRunner()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(SessionKey, false))
            {
                BlackholeInteractionState.DeterministicMode = true;
                _recorder = null;
                _waitStartedAt = EditorApplication.timeSinceStartup;
                _monitoringBenchmark = true;
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                BlackholeInteractionState.DeterministicMode = false;
                SessionState.SetBool(SessionKey, false);
                _monitoringBenchmark = false;
                _recorder = null;
            }
        }

        private static void Tick()
        {
            if (!_monitoringBenchmark || !EditorApplication.isPlaying)
                return;

            if (_recorder == null)
            {
                _recorder = UnityEngine.Object.FindAnyObjectByType<BlackholeBenchmarkRecorder>();
                if (_recorder == null)
                {
                    if (EditorApplication.timeSinceStartup - _waitStartedAt > 20.0)
                        StopWithError("The demo scene did not provide a benchmark recorder.");
                    return;
                }

                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                if (string.IsNullOrEmpty(projectRoot))
                {
                    StopWithError("Unity could not determine the project root for the benchmark output.");
                    return;
                }

                _recorder.SetOutputPathOverride(Path.Combine(projectRoot, OutputFileName));
                _recorder.RunBenchmark();
                Debug.Log($"Benchmark started. The six-case sweep will write {Path.Combine(projectRoot, OutputFileName)}.");
                return;
            }

            if (!_recorder.HasCompleted)
                return;

            if (string.IsNullOrEmpty(_recorder.LastError))
                Debug.Log("Blackhole benchmark complete. Report: " + _recorder.LastExportPath);
            else
                Debug.LogError("Blackhole benchmark stopped: " + _recorder.LastError, _recorder);

            _monitoringBenchmark = false;
            SessionState.SetBool(SessionKey, false);
            EditorApplication.isPlaying = false;
        }

        private static void StopWithError(string message)
        {
            Debug.LogError(message);
            _monitoringBenchmark = false;
            SessionState.SetBool(SessionKey, false);
            EditorApplication.isPlaying = false;
        }
    }

    /// <summary>📷 Waits for 60 rendered frames and exports only the Game View at a fixed 1080p resolution.</summary>
    [InitializeOnLoad]
    internal static class BlackholeScreenshotEditorRunner
    {
        private const string SessionKey = "OmniTwin.BlackholeSimulation.CapturePortfolioScreenshot";
        private const int WarmupFrames = 60;
        private const int OutputWidth = 1920;
        private const int OutputHeight = 1080;
        private static int _lastFrame = -1;
        private static int _framesWaited;
        private static int _stableFileTicks;
        private static long _lastFileLength = -1;
        private static double _startedAt;
        private static string _rawPath;
        private static string _outputPath;
        private static bool _captureRequested;
        private static int _renderFramesRemaining;
        private static int _gameViewWidth;
        private static int _gameViewHeight;
        private static Camera _captureCamera;
        private static RenderTexture _cameraTarget;
        private static RenderTexture _previousCameraTarget;

        static BlackholeScreenshotEditorRunner()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(SessionKey, false))
            {
                BlackholeInteractionState.DeterministicMode = true;
                string root = Directory.GetParent(Application.dataPath)?.FullName;
                if (string.IsNullOrEmpty(root))
                {
                    StopWithError("Unity could not determine the project root for the screenshot.");
                    return;
                }

                string tempDirectory = Path.Combine(root, "Library", "BlackholePortfolioCapture");
                Directory.CreateDirectory(tempDirectory);
                _rawPath = Path.Combine(tempDirectory, "game-view-raw.png");
                _outputPath = Path.Combine(root, "docs", "screenshots", "blackhole-game-view.png");
                if (File.Exists(_rawPath))
                    File.Delete(_rawPath);
                _framesWaited = 0;
                _lastFrame = Time.frameCount;
                _stableFileTicks = 0;
                _lastFileLength = -1;
                _startedAt = EditorApplication.timeSinceStartup;
                _captureRequested = false;
                _renderFramesRemaining = 0;
                _gameViewWidth = Mathf.Max(Screen.width, 1);
                _gameViewHeight = Mathf.Max(Screen.height, 1);
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                BlackholeInteractionState.DeterministicMode = false;
                RestoreCameraTarget();
                SessionState.SetBool(SessionKey, false);
                _captureRequested = false;
            }
        }

        private static void Tick()
        {
            if (!SessionState.GetBool(SessionKey, false) || !EditorApplication.isPlaying)
                return;

            if (!_captureRequested && _cameraTarget == null)
            {
                if (Time.frameCount != _lastFrame)
                {
                    _lastFrame = Time.frameCount;
                    _framesWaited++;
                }
                if (_framesWaited < WarmupFrames)
                    return;

                _captureCamera = Camera.main;
                if (_captureCamera == null)
                {
                    StopWithError("The Main Camera was not available for the portfolio capture.");
                    return;
                }

                _previousCameraTarget = _captureCamera.targetTexture;
                _cameraTarget = new RenderTexture(OutputWidth, OutputHeight, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Default)
                {
                    name = "Blackhole Portfolio Capture 1080p",
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (!_cameraTarget.Create())
                {
                    StopWithError("Unity could not allocate the 1920x1080 camera render target.");
                    return;
                }
                _captureCamera.targetTexture = _cameraTarget;
                _renderFramesRemaining = 2;
                _lastFrame = Time.frameCount;
                EditorApplication.QueuePlayerLoopUpdate();
                return;
            }

            if (!_captureRequested && _cameraTarget != null && _renderFramesRemaining > 0)
            {
                if (Time.frameCount != _lastFrame)
                {
                    _lastFrame = Time.frameCount;
                    _renderFramesRemaining--;
                }
                if (_renderFramesRemaining > 0)
                    return;

                int scale = Mathf.Max(
                    Mathf.CeilToInt(OutputWidth / (float)_gameViewWidth),
                    Mathf.CeilToInt(OutputHeight / (float)_gameViewHeight));
                ScreenCapture.CaptureScreenshot(_rawPath, Mathf.Max(1, scale));
                _captureRequested = true;
                _startedAt = EditorApplication.timeSinceStartup;
                Debug.Log($"Capturing the Game View after {WarmupFrames} warmup frames; writing a {OutputWidth}x{OutputHeight} PNG.");
                return;
            }

            if (EditorApplication.timeSinceStartup - _startedAt > 45.0)
            {
                StopWithError("Unity did not finish writing the Game View screenshot within 45 seconds.");
                return;
            }
            if (!File.Exists(_rawPath))
                return;

            long fileLength = new FileInfo(_rawPath).Length;
            if (fileLength > 0 && fileLength == _lastFileLength)
                _stableFileTicks++;
            else
                _stableFileTicks = 0;
            _lastFileLength = fileLength;
            if (_stableFileTicks < 3)
                return;

            try
            {
                Write1080pPng(_rawPath, _outputPath, _cameraTarget, _gameViewWidth, _gameViewHeight);
                RestoreCameraTarget();
                Debug.Log($"Clean Game View screenshot saved to {_outputPath} ({OutputWidth}x{OutputHeight}).");
                SessionState.SetBool(SessionKey, false);
                _captureRequested = false;
                EditorApplication.isPlaying = false;
            }
            catch (Exception exception)
            {
                StopWithError("The 1080p Game View screenshot could not be processed: " + exception.Message);
            }
        }

        private static void Write1080pPng(string sourcePath, string destinationPath, RenderTexture cameraTarget, int gameViewWidth, int gameViewHeight)
        {
            Texture2D output = new Texture2D(OutputWidth, OutputHeight, TextureFormat.RGBA32, false, false);
            Texture2D overlay = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            RenderTexture previous = RenderTexture.active;
            try
            {
                if (cameraTarget == null || !cameraTarget.IsCreated())
                    throw new InvalidOperationException("The Main Camera render target was lost before readback.");

                RenderTexture.active = cameraTarget;
                output.ReadPixels(new Rect(0, 0, OutputWidth, OutputHeight), 0, 0, false);
                output.Apply(false, false);
                if (!overlay.LoadImage(File.ReadAllBytes(sourcePath), false))
                    throw new InvalidDataException("The captured Game View overlay image is not a valid PNG.");

                Color32[] outputPixels = output.GetPixels32();
                Color32[] overlayPixels = overlay.GetPixels32();
                float sourceScaleX = overlay.width / (float)gameViewWidth;
                float sourceScaleY = overlay.height / (float)gameViewHeight;
                const int panelX = 14;
                const int panelY = 14;
                const int panelWidth = 310;
                const int panelHeight = 164;
                for (int y = 0; y < panelHeight; y++)
                for (int x = 0; x < panelWidth; x++)
                {
                    int sourceX = Mathf.Clamp(Mathf.RoundToInt((panelX + x + 0.5f) * sourceScaleX), 0, overlay.width - 1);
                    int sourceTopY = Mathf.Clamp(Mathf.RoundToInt((panelY + y + 0.5f) * sourceScaleY), 0, overlay.height - 1);
                    int sourceY = overlay.height - 1 - sourceTopY;
                    int destinationX = panelX + x;
                    int destinationY = OutputHeight - 1 - (panelY + y);
                    outputPixels[destinationY * OutputWidth + destinationX] = overlayPixels[sourceY * overlay.width + sourceX];
                }
                output.SetPixels32(outputPixels);
                output.Apply(false, false);
                string directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllBytes(destinationPath, output.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(overlay);
                UnityEngine.Object.DestroyImmediate(output);
                if (File.Exists(sourcePath))
                    File.Delete(sourcePath);
            }
        }

        private static void RestoreCameraTarget()
        {
            if (_captureCamera != null && _captureCamera.targetTexture == _cameraTarget)
                _captureCamera.targetTexture = _previousCameraTarget;
            if (_cameraTarget != null)
            {
                _cameraTarget.Release();
                UnityEngine.Object.DestroyImmediate(_cameraTarget);
            }
            _captureCamera = null;
            _cameraTarget = null;
            _previousCameraTarget = null;
        }

        private static void StopWithError(string message)
        {
            Debug.LogError(message);
            RestoreCameraTarget();
            SessionState.SetBool(SessionKey, false);
            _captureRequested = false;
            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;
        }
    }
}
