using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Game.Demo.AvatarMerge
{
    /// <summary>Demo 展示：持有合并服务、当前 Outfit、目标 SMR。</summary>
    public sealed class AvatarDemoPresenter : MonoBehaviour
    {
        public const string HostName = "AvatarMergeDemoHost";

        [SerializeField] AvatarOutfit _outfit = AvatarOutfit.Default;
        AvatarMergeService _service;
        SkinnedMeshRenderer _smr;
        Transform _rigRoot;
        readonly List<GameObject> _crowd = new List<GameObject>();

        public AvatarOutfit Outfit => _outfit;
        public AvatarMergeService Service => _service;
        public Transform RigRoot => _rigRoot;
        public SkinnedMeshRenderer BodyRenderer => _smr;

        public static AvatarDemoPresenter FindOrCreate()
        {
            var existing = Object.FindFirstObjectByType<AvatarDemoPresenter>();
            if (existing != null) return existing;

            var host = GameObject.Find(HostName);
            if (host == null)
                host = new GameObject(HostName);
            var presenter = host.GetComponent<AvatarDemoPresenter>();
            if (presenter == null)
                presenter = host.AddComponent<AvatarDemoPresenter>();
            return presenter;
        }

        public void EnsureServices()
        {
            if (_service == null)
                _service = new AvatarMergeService(AvatarCatalogFactory.CreateDefault());
        }

        public void BindRig(GameObject rig)
        {
            _rigRoot = rig.transform;
            _smr = AvatarRigBuilder.GetBodyRenderer(_rigRoot);
            if (_smr == null)
                Debug.LogError("[AvatarMerge] Rig missing SkinnedMeshRenderer");
        }

        public async Task<AvatarMergeResult> MergeAsync(AvatarOutfit outfit, bool apply = true)
        {
            EnsureServices();
            if (_rigRoot == null || _smr == null)
                throw new System.InvalidOperationException("Call Setup / BindRig first.");

            _outfit = outfit;
            var result = await _service.BuildAsync(outfit, _rigRoot);
            if (apply)
                _service.Apply(_smr, result, _rigRoot);
            return result;
        }

        public Task<AvatarMergeResult> MergeDefaultAsync()
        {
            return MergeAsync(AvatarOutfit.Default);
        }

        public Task<AvatarMergeResult> SwitchPartAsync(AvatarPartType part, int newId)
        {
            var next = _outfit.WithPart(part, newId);
            Debug.Log($"[AvatarMerge] Switch {part} → {newId} (outfit {next})");
            return MergeAsync(next);
        }

        public Task CycleAllPartsAsync()
        {
            return CycleAllPartsInternalAsync();
        }

        async Task CycleAllPartsInternalAsync()
        {
            var parts = new[]
            {
                AvatarPartType.Hair, AvatarPartType.Top, AvatarPartType.Bottom,
                AvatarPartType.Shoes, AvatarPartType.Hat, AvatarPartType.Acc
            };
            foreach (var part in parts)
            {
                int current = _outfit.GetPartId(part);
                int alt = current % 2 == 1 ? current + 1 : current - 1;
                await SwitchPartAsync(part, alt);
            }
        }

        public async Task SpawnCrowdAsync(int count = 40)
        {
            EnsureServices();
            ClearCrowd();

            var sw = Stopwatch.StartNew();
            int cols = 8;
            for (int i = 0; i < count; i++)
            {
                var rig = AvatarRigBuilder.BuildRig(transform);
                rig.name = $"Crowd_{i:00}";
                int row = i / cols;
                int col = i % cols;
                rig.transform.localPosition = new Vector3(col * 1.2f - 4f, 0f, row * 1.2f + 2f);

                // Share default outfit result when possible
                var result = await _service.BuildAsync(AvatarOutfit.Default, rig.transform);
                var smr = AvatarRigBuilder.GetBodyRenderer(rig.transform);
                _service.Apply(smr, result, rig.transform);
                _crowd.Add(rig);
            }
            sw.Stop();
            Debug.Log($"[AvatarMerge] Spawned {_crowd.Count} instances in {sw.ElapsedMilliseconds}ms; cache hits={_service.Cache.HitCount} misses={_service.Cache.MissCount}");
        }

        public void ClearCrowd()
        {
            for (int i = 0; i < _crowd.Count; i++)
            {
                if (_crowd[i] != null)
                    DestroyImmediate(_crowd[i]);
            }
            _crowd.Clear();
        }

        public void LogCacheStats()
        {
            EnsureServices();
            var c = _service.Cache;
            Debug.Log($"[AvatarMerge] Cache stats: count={c.Count}/{c.Capacity} hits={c.HitCount} misses={c.MissCount}");
        }
    }
}
