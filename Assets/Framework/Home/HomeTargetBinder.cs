using UnityEngine;

namespace Game.Framework.Home
{
    /// <summary>
    /// 灰盒目标上的显式稳定 ID 绑定。禁止用 sibling 索引 / 名称解析持久 ID。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HomeTargetBinder : MonoBehaviour
    {
        [SerializeField] int _instanceId;
        [SerializeField] string _kind = HomeInstanceKinds.Plot;
        [SerializeField] bool _interactionEnabled = true;

        public int InstanceId => _instanceId;
        public string Kind => _kind ?? "";

        /// <summary>显式绑定且未销毁；禁用交互时仍视为已绑定（遮挡），但解析失败。</summary>
        public bool IsBound =>
            _instanceId > 0
            && !string.IsNullOrEmpty(_kind);

        public bool InteractionEnabled => _interactionEnabled;

        /// <summary>仅装配/烘焙调用；运行时不得用数组序重写。</summary>
        public void BindExplicit(int instanceId, string kind)
        {
            if (instanceId <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(instanceId));
            if (string.IsNullOrEmpty(kind))
                throw new System.ArgumentException("kind required", nameof(kind));
            _instanceId = instanceId;
            _kind = kind;
        }

        /// <summary>禁用后命中仍遮挡后方目标，但不产出有效 HomeTarget。</summary>
        public void SetInteractionEnabled(bool enabled)
        {
            _interactionEnabled = enabled;
        }
    }
}
