using System.Linq;
using System.Reflection;

namespace CloudIsle.Tests
{
    public sealed class SharedReferenceTests
    {
        /// <summary>
        /// 确认测试工程能加载共享程序集，并且该程序集没有引用 Unity。
        /// </summary>
        [Test]
        public void GameShared_DoesNotReferenceUnityEngine()
        {
            // MsgRegistry 来自 T04 生成代码，位于 Game.Shared。
            Assembly assembly = typeof(CloudIsle.Proto.MsgRegistry).Assembly;
            // AssemblyName.Name 在反射 API 里是 string?，这里保持可空，避免测试工程的 Nullable 警告。
            string?[] names = assembly.GetReferencedAssemblies()
                .Select(item => item.Name)
                .ToArray();

            Assert.That(assembly.GetName().Name, Is.EqualTo("Game.Shared"));
            Assert.That(names, Does.Not.Contain("UnityEngine"));
            Assert.That(names, Does.Not.Contain("UnityEditor"));
        }
    }
}
