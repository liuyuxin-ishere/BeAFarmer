using System.Collections;
using Game.Framework.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Framework.PlayModeTests
{
    /// <summary>
    /// T51 锚点逻辑（headless 可跑）。GUI 截图由 StageC1Tools.BatchCaptureUiShots 在有图形时落盘，
    /// 避免 -nographics 下 ScreenCapture 挂死。
    /// </summary>
    public sealed class SafeAreaShotTests
    {
        [UnityTest]
        public IEnumerator SafeArea_ThreeAspectMath_AndNonZeroInsets()
        {
            var adapter = UiRoot.Ensure().SafeArea;

            adapter.Apply(new Rect(0, 0, 1280, 720), new Vector2Int(1280, 720));
            Assert.That(adapter.AnchorMin, Is.EqualTo(Vector2.zero));
            Assert.That(adapter.AnchorMax, Is.EqualTo(Vector2.one));

            adapter.Apply(new Rect(0, 0, 1920, 1080), new Vector2Int(1920, 1080));
            Assert.That(adapter.AnchorMax.x, Is.EqualTo(1f).Within(0.001f));

            adapter.Apply(new Rect(0, 0, 1080, 1920), new Vector2Int(1080, 1920));
            Assert.That(adapter.AnchorMax.y, Is.EqualTo(1f).Within(0.001f));

            adapter.Apply(new Rect(80, 60, 920, 1800), new Vector2Int(1080, 1920));
            Assert.That(adapter.AnchorMin.x, Is.EqualTo(80f / 1080f).Within(0.001f));
            Assert.That(adapter.AnchorMin.y, Is.EqualTo(60f / 1920f).Within(0.001f));
            Assert.That(adapter.AnchorMax.x, Is.EqualTo(1000f / 1080f).Within(0.001f));
            Assert.That(adapter.AnchorMax.y, Is.EqualTo(1860f / 1920f).Within(0.001f));

            adapter.Apply(new Rect(0, 0, 0, 0), new Vector2Int(0, 0));
            Assert.That(adapter.AnchorMin, Is.EqualTo(Vector2.zero));
            Assert.That(adapter.AnchorMax, Is.EqualTo(Vector2.one));
            yield return null;
        }
    }
}
