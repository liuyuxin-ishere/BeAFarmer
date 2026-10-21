using System.Collections;
using UnityEngine;
using YooAsset;

namespace Game.Framework.Resource
{
    /// <summary>
    /// PoC #3：OfflinePlayMode 加载 StreamingAssets 内置包，实例化 PatchCube。
    /// </summary>
    public sealed class YooAssetPoCBoot : MonoBehaviour
    {
        public const string PackageName = "DefaultPackage";
        public const string AssetAddress = "PatchCube";

        [SerializeField] string _status = "starting...";
        [SerializeField] string _loadedVersion = "-";
        Color _loadedColor = Color.white;

        public string Status => _status;
        public string LoadedVersion => _loadedVersion;

        void Start()
        {
            Debug.Log("[YooAsset PoC] Start");
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            _status = "YooAssets.Initialize";
            Debug.Log($"[YooAsset PoC] {_status}");
            YooAssets.Initialize();

            // 编辑器关闭 Domain Reload 时可能残留 Package；正常 Play 则直接复用/创建
            ResourcePackage package;
            if (!YooAssets.TryGetPackage(PackageName, out package) || package == null)
                package = YooAssets.CreatePackage(PackageName);

            _status = "InitializePackageAsync (OfflinePlayMode)";
            Debug.Log($"[YooAsset PoC] {_status}");
            var initOptions = new OfflinePlayModeOptions();
            initOptions.BuiltinFileSystemParameters = FileSystemParameters.CreateDefaultBuiltinFileSystemParameters();
            var initOp = package.InitializePackageAsync(initOptions);
            yield return initOp;
            if (initOp.Status != EOperationStatus.Succeeded)
            {
                _status = $"Init FAILED: {initOp.Error}";
                Debug.LogError($"[YooAsset PoC] {_status}");
                yield break;
            }

            _status = "RequestPackageVersionAsync";
            Debug.Log($"[YooAsset PoC] {_status}");
            var versionOp = package.RequestPackageVersionAsync();
            yield return versionOp;
            if (versionOp.Status != EOperationStatus.Succeeded)
            {
                _status = $"RequestVersion FAILED: {versionOp.Error}";
                Debug.LogError($"[YooAsset PoC] {_status}");
                yield break;
            }

            _status = $"LoadPackageManifestAsync ({versionOp.PackageVersion})";
            Debug.Log($"[YooAsset PoC] {_status}");
            var manifestOp = package.LoadPackageManifestAsync(new LoadPackageManifestOptions(versionOp.PackageVersion, 60));
            yield return manifestOp;
            if (manifestOp.Status != EOperationStatus.Succeeded)
            {
                _status = $"LoadManifest FAILED: {manifestOp.Error}";
                Debug.LogError($"[YooAsset PoC] {_status}");
                yield break;
            }

            _status = $"LoadAssetAsync {AssetAddress}";
            Debug.Log($"[YooAsset PoC] {_status}");
            var handle = package.LoadAssetAsync<GameObject>(AssetAddress);
            yield return handle;
            if (handle.Status != EOperationStatus.Succeeded)
            {
                _status = $"LoadAsset FAILED: {handle.Error}";
                Debug.LogError($"[YooAsset PoC] {_status}");
                yield break;
            }

            var go = handle.InstantiateSync();
            go.transform.position = Vector3.zero;

            var marker = go.GetComponent<PatchCubeMarker>();
            if (marker != null)
            {
                _loadedVersion = marker.Version;
                _loadedColor = marker.Color;
            }

            _status = $"OK package={versionOp.PackageVersion} prefab={_loadedVersion}";
            Debug.Log($"[YooAsset PoC] {_status}");
        }

        void OnGUI()
        {
            const int w = 640;
            const int h = 120;
            GUI.Box(new Rect(16, 16, w, h), "YooAsset PoC #3 — Offline / Local Patch");
            GUI.Label(new Rect(28, 48, w - 24, 28), _status);
            GUI.color = _loadedColor;
            GUI.Label(new Rect(28, 80, w - 24, 28), $"Loaded Prefab Version: {_loadedVersion}");
            GUI.color = Color.white;
        }
    }
}
