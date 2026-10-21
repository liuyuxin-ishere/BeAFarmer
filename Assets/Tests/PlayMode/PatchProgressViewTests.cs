using System.Collections;
using Game.Framework.Boot;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Framework.PlayModeTests
{
    /// <summary>PatchProgressView：零总量、阶段映射、消毒、重试门闸、EventSystem 点击。</summary>
    public sealed class PatchProgressViewTests
    {
        [UnityTest]
        public IEnumerator Render_ZeroTotals_NoDivideByZero_AndReadyFull()
        {
            var view = PatchProgressView.CreateRuntime();
            view.Render(new PatchProgress
            {
                Stage = "Download:GameLua",
                DownloadedBytes = 0,
                TotalBytes = 0,
                CompletedFiles = 0,
                TotalFiles = 0
            });
            Assert.That(view.LastStage, Is.EqualTo("Download:GameLua"));
            Assert.That(view.StatusText.text, Does.Not.Contain("GameLua"));
            Assert.That(view.StatusText.text, Does.Contain("下载"));
            view.Render(new PatchProgress
            {
                Stage = "Ready",
                DownloadedBytes = 0,
                TotalBytes = 0,
                CompletedFiles = 0,
                TotalFiles = 0
            });
            Assert.That(view.LastStage, Is.EqualTo("Ready"));
            Slider slider = view.GetComponentInChildren<Slider>(true);
            Assert.That(slider, Is.Not.Null);
            Assert.That(slider.value, Is.EqualTo(1f));
            Object.Destroy(view.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ShowError_Sanitize_AndRetryGate_BlocksParallel()
        {
            var view = PatchProgressView.CreateRuntime();
            int clicks = 0;
            view.ShowError("secret password=abc\n  at Some.Stack", () => { clicks++; });
            Assert.That(view.StatusText.text, Does.Not.Contain("at Some"));
            Assert.That(view.StatusText.text, Does.Not.Contain("abc"));
            Assert.That(view.RetryButton.gameObject.activeSelf, Is.True);

            view.InvokeRetryForTests();
            view.InvokeRetryForTests();
            view.InvokeRetryForTests();
            Assert.That(clicks, Is.EqualTo(1));
            Assert.That(view.RetryGateTaken, Is.True);

            view.ReleaseRetryGate();
            view.InvokeRetryForTests();
            Assert.That(clicks, Is.EqualTo(2));

            Object.Destroy(view.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Retry_ViaEventSystem_PointerClick_SingleTask()
        {
            var view = PatchProgressView.CreateRuntime();
            Assert.That(EventSystem.current, Is.Not.Null);
            int clicks = 0;
            view.ShowError("网络下载失败", () => { clicks++; });
            Assert.That(view.TryClickRetryViaEventSystem(), Is.True);
            Assert.That(view.TryClickRetryViaEventSystem(), Is.True);
            Assert.That(clicks, Is.EqualTo(1));
            Object.Destroy(view.gameObject);
            yield return null;
        }

        [Test]
        public void SanitizeUserMessage_RemovesSecretValues()
        {
            string cleaned = PatchProgressView.SanitizeUserMessage(
                "fail Bearer token-value password=secret123\n  at Foo.Bar()");
            Assert.That(cleaned, Does.Not.Contain("at Foo"));
            Assert.That(cleaned, Does.Not.Contain("token-value"));
            Assert.That(cleaned, Does.Not.Contain("secret123"));
            Assert.That(cleaned, Does.Contain("Bearer ***"));
            Assert.That(cleaned, Does.Contain("password=***"));
        }

        [Test]
        public void MapStage_HidesInternalPackageName()
        {
            Assert.That(PatchProgressView.MapStageToUserText("Initialize:GameLua"), Does.Not.Contain("GameLua"));
            Assert.That(PatchProgressView.MapFailureReason("Download failed HTTP 404"), Does.Contain("网络"));
        }
    }
}
