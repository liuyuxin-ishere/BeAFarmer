using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.UI
{
    /// <summary>
    /// 固定 C# View 桥。只操作已有组件；SubscribeClick 返回退订租约。
    /// 签名冻结进 bridgeContract m1-bridge-0.3，补丁不得改此类型。
    /// </summary>
    public sealed class ViewHost : IDisposable
    {
        readonly GameObject _root;
        readonly List<ClickSubscription> _clicks = new List<ClickSubscription>();
        bool _disposed;

        /// <summary>根物体。Dispose 后为 null。</summary>
        public GameObject Root => _disposed ? null : _root;

        /// <summary>当前未 Dispose 的点击订阅数（本实例）。</summary>
        public int LiveClickCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _clicks.Count; i++)
                {
                    if (_clicks[i] != null)
                        n++;
                }

                return n;
            }
        }

        public ViewHost(GameObject root)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
        }

        /// <summary>按相对路径绑定 Transform。空路径返回根。</summary>
        public Transform Bind(string path)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(path))
                return _root.transform;
            Transform t = _root.transform.Find(path);
            if (t == null)
                throw new InvalidOperationException("bind-missing path=" + path);
            return t;
        }

        /// <summary>设置 Text 或 TMP 兼容 UGUI Text。</summary>
        public void SetText(string path, string text)
        {
            ThrowIfDisposed();
            Transform t = Bind(path);
            var uiText = t.GetComponent<Text>();
            if (uiText == null)
                throw new InvalidOperationException("SetText needs Text path=" + path);
            uiText.text = text ?? "";
        }

        /// <summary>设置 Selectable.interactable。</summary>
        public void SetInteractable(string path, bool interactable)
        {
            ThrowIfDisposed();
            Transform t = Bind(path);
            var selectable = t.GetComponent<Selectable>();
            if (selectable == null)
                throw new InvalidOperationException("SetInteractable needs Selectable path=" + path);
            selectable.interactable = interactable;
        }

        /// <summary>读取 InputField 文本。无组件时抛错。</summary>
        public string GetInputText(string path)
        {
            ThrowIfDisposed();
            Transform t = Bind(path);
            var input = t.GetComponent<InputField>();
            if (input == null)
                throw new InvalidOperationException("GetInputText needs InputField path=" + path);
            return input.text ?? "";
        }

        /// <summary>写入 InputField 文本。</summary>
        public void SetInputText(string path, string text)
        {
            ThrowIfDisposed();
            Transform t = Bind(path);
            var input = t.GetComponent<InputField>();
            if (input == null)
                throw new InvalidOperationException("SetInputText needs InputField path=" + path);
            input.text = text ?? "";
        }

        /// <summary>设置 InputField 内容类型（0=Standard，1=Password 等，对齐 InputField.ContentType 整型）。</summary>
        public void SetInputContentType(string path, int contentType)
        {
            ThrowIfDisposed();
            Transform t = Bind(path);
            var input = t.GetComponent<InputField>();
            if (input == null)
                throw new InvalidOperationException("SetInputContentType needs InputField path=" + path);
            input.contentType = (InputField.ContentType)contentType;
            input.ForceLabelUpdate();
        }

        /// <summary>
        /// 订阅 Button 点击。返回租约；关闭窗口时 ViewHost.Dispose 会清理未导出租约。
        /// </summary>
        public ClickSubscription SubscribeClick(string path, LuaClickHandler handler)
        {
            ThrowIfDisposed();
            Transform t = Bind(path);
            var button = t.GetComponent<Button>();
            if (button == null)
                throw new InvalidOperationException("SubscribeClick needs Button path=" + path);
            var sub = new ClickSubscription(button, handler);
            _clicks.Add(sub);
            return sub;
        }

        /// <summary>退订并销毁根物体。幂等。</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            for (int i = 0; i < _clicks.Count; i++)
            {
                try
                {
                    _clicks[i]?.Dispose();
                }
                catch
                {
                }
            }

            _clicks.Clear();
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ViewHost));
        }
    }
}
