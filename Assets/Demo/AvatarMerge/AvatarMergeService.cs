using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Game.Demo.AvatarMerge
{
    public sealed class AvatarMergeService : IAvatarMergeService
    {
        static readonly AvatarPartType[] MergeParts =
        {
            AvatarPartType.Hair,
            AvatarPartType.Top,
            AvatarPartType.Bottom,
            AvatarPartType.Shoes,
            AvatarPartType.Hat,
            AvatarPartType.Acc
        };

        readonly AvatarPartCatalog _catalog;
        readonly IAvatarPartLoader _loader;
        readonly IAvatarMergeCache _cache;
        readonly Shader _shader;

        public IAvatarMergeCache Cache => _cache;

        public AvatarMergeService(
            AvatarPartCatalog catalog,
            IAvatarPartLoader loader = null,
            IAvatarMergeCache cache = null,
            Shader shader = null)
        {
            _catalog = catalog ?? AvatarCatalogFactory.CreateDefault();
            _loader = loader ?? new AvatarPartLoader();
            _cache = cache ?? new AvatarMergeCache(64);
            _shader = shader ?? FindUnlitShader();
        }

        public async Task<AvatarMergeResult> BuildAsync(
            AvatarOutfit outfit,
            Transform rigRoot,
            CancellationToken ct = default)
        {
            long hash = outfit.GetOutfitHash();
            if (_cache.TryGet(hash, out var cached))
            {
                Debug.Log($"[AvatarMerge] cache hit hash={hash} outfit={outfit}");
                return cached;
            }

            var sw = Stopwatch.StartNew();
            ct.ThrowIfCancellationRequested();

            // 同步合并（Editor 菜单可安全 await）；接口仍返回 Task 以对齐正式异步加载。
            var runtimes = new List<AvatarPartRuntime>(6);
            var textures = new List<Texture2D>(6);

            foreach (var part in MergeParts)
            {
                ct.ThrowIfCancellationRequested();
                int id = outfit.GetPartId(part);
                var asset = _catalog.Get(part, id) ?? _catalog.Get(id);
                if (asset == null)
                    throw new System.InvalidOperationException($"Missing part asset part={part} id={id}");

                var loadTask = _loader.LoadAsync(asset);
                var runtime = loadTask.IsCompleted
                    ? loadTask.Result
                    : loadTask.GetAwaiter().GetResult();
                runtimes.Add(runtime);
                textures.Add(runtime.Albedo);
            }

            var atlasBake = AvatarAtlasBaker.Bake(textures, 512);
            var tempMeshes = new List<Mesh>(runtimes.Count);

            for (int i = 0; i < runtimes.Count; i++)
            {
                var src = runtimes[i].Mesh;
                var temp = Object.Instantiate(src);
                temp.name = src.name + "_uv";
                AvatarAtlasBaker.RemapUVs(temp, atlasBake.Uvs[i]);
                if (temp.bindposes == null || temp.bindposes.Length != AvatarPlaceholderFactory.RigBoneNames.Length)
                {
                    var bp = new Matrix4x4[AvatarPlaceholderFactory.RigBoneNames.Length];
                    for (int b = 0; b < bp.Length; b++) bp[b] = Matrix4x4.identity;
                    temp.bindposes = bp;
                }
                tempMeshes.Add(temp);
            }

            var combined = ManualSkinnedMerge(tempMeshes);
            combined.name = $"Merged_{hash}";

            var bones = AvatarRigBuilder.CollectBones(rigRoot);
            var smr = AvatarRigBuilder.GetBodyRenderer(rigRoot);
            combined.bindposes = AvatarRigBuilder.BuildBindposes(smr != null ? smr.transform : rigRoot, bones);

            var mat = new Material(_shader)
            {
                name = $"AvatarMat_{hash}",
                mainTexture = atlasBake.Atlas,
                color = Color.white
            };
            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", atlasBake.Atlas);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", Color.white);

            var result = new AvatarMergeResult
            {
                OutfitHash = hash,
                CombinedMesh = combined,
                Atlas = atlasBake.Atlas,
                Material = mat
            };

            foreach (var t in tempMeshes)
                Object.DestroyImmediate(t);
            foreach (var r in runtimes)
            {
                if (r.Mesh != null)
                    Object.DestroyImmediate(r.Mesh);
            }

            for (int i = 0; i < textures.Count; i++)
            {
                var tex = textures[i];
                if (tex != null && tex != atlasBake.Atlas && !IsAssetTexture(tex))
                    Object.DestroyImmediate(tex);
            }

            _cache.Put(hash, result);
            sw.Stop();
            Debug.Log($"[AvatarMerge] cache miss → merged hash={hash} outfit={outfit} verts={combined.vertexCount} atlas={atlasBake.Size} ms={sw.ElapsedMilliseconds}");
            return await Task.FromResult(result);
        }

        public void Apply(SkinnedMeshRenderer target, AvatarMergeResult result, Transform rigRoot)
        {
            if (target == null) throw new System.ArgumentNullException(nameof(target));
            if (result == null) throw new System.ArgumentNullException(nameof(result));

            var bones = AvatarRigBuilder.CollectBones(rigRoot);
            result.CombinedMesh.bindposes = AvatarRigBuilder.BuildBindposes(target.transform, bones);

            target.sharedMesh = result.CombinedMesh;
            target.sharedMaterial = result.Material;
            target.bones = bones;
            target.rootBone = bones[1];
            target.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            target.receiveShadows = false;
            target.localBounds = result.CombinedMesh.bounds;
        }

        static Mesh ManualSkinnedMerge(List<Mesh> parts)
        {
            int vertCount = 0;
            int triCount = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                vertCount += parts[i].vertexCount;
                triCount += parts[i].triangles.Length;
            }

            var verts = new List<Vector3>(vertCount);
            var norms = new List<Vector3>(vertCount);
            var uvs = new List<Vector2>(vertCount);
            var weights = new List<BoneWeight>(vertCount);
            var tris = new List<int>(triCount);

            for (int i = 0; i < parts.Count; i++)
            {
                var m = parts[i];
                int baseVert = verts.Count;
                var mv = m.vertices;
                var mn = m.normals;
                var mu = m.uv;
                var mw = m.boneWeights;
                var mt = m.triangles;

                for (int v = 0; v < mv.Length; v++)
                {
                    verts.Add(mv[v]);
                    norms.Add(mn != null && mn.Length == mv.Length ? mn[v] : Vector3.up);
                    uvs.Add(mu != null && mu.Length == mv.Length ? mu[v] : new Vector2(0.5f, 0.5f));
                    weights.Add(mw != null && mw.Length == mv.Length
                        ? mw[v]
                        : new BoneWeight { boneIndex0 = 1, weight0 = 1f });
                }

                for (int t = 0; t < mt.Length; t++)
                    tris.Add(mt[t] + baseVert);
            }

            var mesh = new Mesh { name = "MergedManual" };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.boneWeights = weights.ToArray();
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static Shader FindUnlitShader()
        {
            var s = Shader.Find("Universal Render Pipeline/Unlit");
            if (s != null) return s;
            s = Shader.Find("Unlit/Texture");
            if (s != null) return s;
            return Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
        }

        static bool IsAssetTexture(Texture2D tex)
        {
            // Demo textures are runtime-generated; Resources assets keep hideFlags None + non-zero instance from disk.
            return tex != null && (tex.hideFlags & HideFlags.DontSave) == 0 && tex.name.StartsWith("tex_") == false;
        }
    }
}
