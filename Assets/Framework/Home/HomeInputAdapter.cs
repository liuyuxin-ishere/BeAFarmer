using System;
using UnityEngine;

namespace Game.Framework.Home
{
    /// <summary>
    /// U02：家园射线命中 → 稳定 HomeTarget。只解析意图目标，不发网络、不改本地资产。
    /// </summary>
    public sealed class HomeInputAdapter
    {
        public const float DefaultMaxDistance = 200f;

        readonly Camera _camera;
        readonly LayerMask _mask;
        readonly float _maxDistance;
        bool _enabled = true;

        public HomeInputAdapter(Camera camera, LayerMask mask, float maxDistance = DefaultMaxDistance)
        {
            _camera = camera != null ? camera : throw new ArgumentNullException(nameof(camera));
            _mask = mask;
            _maxDistance = maxDistance > 0f ? maxDistance : DefaultMaxDistance;
        }

        public bool InputEnabled => _enabled;

        /// <summary>关闭后 TryResolveTarget 恒 false；由装配层同时取消拖拽手势。</summary>
        public void SetInputEnabled(bool enabled)
        {
            _enabled = enabled;
        }

        /// <summary>
        /// 沿射线解析显式绑定目标。首个带有效 HomeTargetBinder 的命中胜出；
        /// 未知/已销毁/无绑定 → false。
        /// </summary>
        public bool TryResolveTarget(Ray ray, out HomeTarget target)
        {
            target = default;
            if (!_enabled)
                return false;

            if (!Physics.Raycast(ray, out RaycastHit hit, _maxDistance, _mask, QueryTriggerInteraction.Ignore))
                return false;

            if (hit.collider == null)
                return false;

            HomeTargetBinder binder = hit.collider.GetComponentInParent<HomeTargetBinder>();
            // 无绑定/已销毁：最近实体仍作遮挡，不穿透到后方合法目标
            if (binder == null || !binder.IsBound)
                return false;

            // 仅接纳冻结注册表内稳定 ID + 显式 kind；伪造正 ID/错误 kind → false（仍遮挡）
            if (!TryValidateRegistered(binder, out target))
                return false;

            return true;
        }

        /// <summary>
        /// Plot：instanceId 落在冻结 24 布局且 kind≡Plot。
        /// Node：instanceId 落在冻结 13 布局且 kind≡Node（不得用 typeId/序号冒充 instanceId）。
        /// 禁用交互 / 未知 kind / 布局外 ID / 错 kind 一律拒绝（命中 collider 仍遮挡）。
        /// </summary>
        static bool TryValidateRegistered(HomeTargetBinder binder, out HomeTarget target)
        {
            target = default;
            if (binder == null)
                return false;
            if (!binder.InteractionEnabled)
                return false;

            int id = binder.InstanceId;
            string kind = binder.Kind ?? "";
            if (string.Equals(kind, HomeInstanceKinds.Plot, StringComparison.Ordinal))
            {
                if (!HomePlotLayout.TryGet(id, out _))
                    return false;
                target = new HomeTarget(id, HomeInstanceKinds.Plot);
                return true;
            }

            if (string.Equals(kind, HomeInstanceKinds.Node, StringComparison.Ordinal))
            {
                if (!HomeNodeLayout.TryGet(id, out _))
                    return false;
                target = new HomeTarget(id, HomeInstanceKinds.Node);
                return true;
            }

            return false;
        }

        /// <summary>屏幕像素 → 相机射线再解析。</summary>
        public bool TryResolveScreenPoint(Vector2 screenPoint, out HomeTarget target)
        {
            if (_camera == null)
            {
                target = default;
                return false;
            }

            Ray ray = _camera.ScreenPointToRay(new Vector3(screenPoint.x, screenPoint.y, 0f));
            return TryResolveTarget(ray, out target);
        }
    }
}
