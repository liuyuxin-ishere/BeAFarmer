using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Framework.Boot
{
    /// <summary>
    /// PoC #2：加载 Game.Hotfix 并反射调用入口。
    /// Framework 不编译期引用 Hotfix；真机从 StreamingAssets 加载 DLL。
    /// </summary>
    public sealed class HotfixLoader : MonoBehaviour
    {
        const string HotfixAssemblyName = "Game.Hotfix";
        const string HotfixDllFileName = "Game.Hotfix.dll.bytes";
        const string EntryTypeName = "Game.Hotfix.Entry";
        const string EntryMethodName = "Start";

        [SerializeField] Text statusText;

        void Start()
        {
            try
            {
                Assembly hotfixAssembly = LoadHotfixAssembly();
                InvokeHotfixEntry(hotfixAssembly);
            }
            catch (Exception e)
            {
                SetStatus($"[Hotfix] FAILED: {e.Message}");
                Debug.LogException(e);
            }
        }

        static Assembly LoadHotfixAssembly()
        {
#if UNITY_EDITOR
            Assembly existing = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == HotfixAssemblyName);
            if (existing != null)
                return existing;
#endif
            string path = Path.Combine(Application.streamingAssetsPath, HotfixDllFileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Hotfix DLL not found: {path}");

            return Assembly.Load(File.ReadAllBytes(path));
        }

        void InvokeHotfixEntry(Assembly assembly)
        {
            Type entryType = assembly.GetType(EntryTypeName);
            if (entryType == null)
                throw new InvalidOperationException($"Type not found: {EntryTypeName}");

            MethodInfo start = entryType.GetMethod(EntryMethodName, BindingFlags.Public | BindingFlags.Static);
            if (start == null)
                throw new InvalidOperationException($"Method not found: {EntryTypeName}.{EntryMethodName}");

            object message = start.Invoke(null, new object[] { statusText });
            SetStatus(message as string ?? "[Hotfix] Entry.Start returned null");
        }

        void SetStatus(string message)
        {
            Debug.Log(message);
            if (statusText != null)
                statusText.text = message;
        }
    }
}
