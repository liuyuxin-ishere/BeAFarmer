using System.Collections;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Resource;
using Game.Framework.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using XLua;
using CloudIsle.Contracts;
using YooAsset;

namespace Game.Framework.PlayModeTests
{
    /// <summary>Login/HomeHud Prefab + Lua 控制器最小可见性。</summary>
    public sealed class LoginUiPlayModeTests
    {
        ResMgr _resources;
        LuaEnv _lua;
        UiManager _ui;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _resources = new ResMgr { PlayMode = ResourcePlayMode.EditorSimulate };
            var manifest = new VersionManifest { BaseUrl = "http://127.0.0.1/unused", PackageVersion = "2" };
            yield return _resources.InitializeAsync(manifest, CancellationToken.None).ToCoroutine();
            ResourcePackage package = YooAssets.GetPackage(ResMgr.PackageName);
            var ver = package.RequestPackageVersionAsync();
            yield return ver;
            var man = package.LoadPackageManifestAsync(new LoadPackageManifestOptions(ver.PackageVersion, 60));
            yield return man;

            _lua = new LuaEnv();
            _lua.AddLoader(LoadLuaFromAssets);
            UiRoot.Ensure();
            _ui = UiBridge.EnsureManager(_resources, _lua);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _ui?.Dispose();
            _ui = null;
            if (_lua != null)
            {
                _lua.Dispose();
                _lua = null;
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator LoginPrefab_Opens_PasswordMasked_ButtonsLarge()
        {
            _ui.Register(new UiWindowRegistration
            {
                WindowId = "Login",
                PrefabAddress = "LoginWindow",
                Layer = UILayer.Window,
                ControllerModule = "UI/Windows/LoginWindow",
                CanGoBack = false
            });

            int code = -9;
            yield return _ui.OpenAwait("Login", "", CancellationToken.None).ToCoroutine(r => code = r);
            Assert.That(code, Is.EqualTo(0));
            var win = GameObject.Find("Win_Login");
            Assert.That(win, Is.Not.Null);
            var pass = win.transform.Find("Password")?.GetComponent<InputField>();
            Assert.That(pass, Is.Not.Null);
            Assert.That(pass.contentType, Is.EqualTo(InputField.ContentType.Password));
            var loginBtn = win.transform.Find("LoginButton") as RectTransform;
            Assert.That(loginBtn, Is.Not.Null);
            Assert.That(loginBtn.rect.height, Is.GreaterThan(40f));

            // nographics batch 无法建 RT；有图形会话另跑 StageC2Tools 截图。此处落盘布局证据。
            string shotDir = Path.GetFullPath("Docs/Acceptance/M1/20261020-xLua/StageC2/shots");
            Directory.CreateDirectory(shotDir);
            string report = Path.Combine(shotDir, "login-layout.txt");
            File.WriteAllText(report,
                "passwordMasked=" + (pass.contentType == InputField.ContentType.Password) + "\n"
                + "loginBtnH=" + loginBtn.rect.height + "\n"
                + "title=" + (win.transform.Find("Title")?.GetComponent<Text>()?.text ?? "") + "\n"
                + "nographicsCapture=skipped\n",
                new System.Text.UTF8Encoding(false));
            Assert.That(File.Exists(report), Is.True);

            bool closed = false;
            _ui.Close("Login", (c, _) => closed = true);
            yield return null;
            Assert.That(closed, Is.True);
        }

        static byte[] LoadLuaFromAssets(ref string filepath)
        {
            string rel = (filepath ?? "").Replace('.', '/').Replace('\\', '/');
            if (rel.EndsWith(".txt", System.StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(0, rel.Length - 4);
            string path = Path.Combine(Application.dataPath, "Lua", rel + ".txt");
            if (!File.Exists(path))
                return null;
            filepath = path;
            return File.ReadAllBytes(path);
        }
    }
}
